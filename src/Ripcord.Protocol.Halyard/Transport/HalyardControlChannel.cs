using System.IO;
using System.Net;
using Ripcord.Core.Net.Tcp;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;

namespace Ripcord.Protocol.Halyard.Transport;

/// <summary>
/// The direct-console control channel: sends the /sess request/response handshake and then carries
/// RPCS frames. Two transports exist (docs/protocol/ps5-local-discovery.md): plain TCP for a
/// directly-reachable LAN console (implemented here) and RUDP-over-UDP for cloud/relay paths (a
/// later addition). The application-layer protocol is identical either way.
/// </summary>
public interface IHalyardControlChannel : IAsyncDisposable
{
    Task ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken);

    Task<SessResponse> SendRequestAsync(SessRequest request, CancellationToken cancellationToken);

    /// <summary>Send one message on the persistent binary control channel (after the /sess/ctrl handshake).</summary>
    Task SendCtrlMessageAsync(HalyardCtrlMessage message, CancellationToken cancellationToken);

    /// <summary>Read the next control-channel message, or null if the connection has closed.</summary>
    Task<HalyardCtrlMessage?> ReadCtrlMessageAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The v2 during-stream RPCS channel's raw frame I/O, over whatever connection carried <c>/sess/ctrl</c> (it
/// stays open). Only the plain-TCP channel implements it today; the datagram (rendezvous) channels do not yet, so
/// the session checks for it with <c>is IHalyardRpcsTransport</c>. Frames are the
/// <see cref="HalyardRpcsChannel"/> wire form (<c>"RPCS"‖be32(len)‖ct‖tag16</c>); the GCM seal/open and the
/// per-direction counters live in <see cref="HalyardRpcsChannel"/>, so this moves bytes only.
/// </summary>
public interface IHalyardRpcsTransport
{
    /// <summary>Send one complete RPCS frame (serialized by <see cref="HalyardRpcsChannel.Seal"/>).</summary>
    Task SendRpcsFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

    /// <summary>
    /// Read the next complete RPCS frame (reassembled from the <c>"RPCS"‖be32(len)</c> header), or
    /// <see langword="null"/> if the connection closed. Throws <see cref="InvalidDataException"/> if the bytes on
    /// the channel are not a well-formed RPCS frame.
    /// </summary>
    Task<byte[]?> ReadRpcsFrameAsync(CancellationToken cancellationToken);
}

/// <summary>Plain-TCP control channel for a directly-reachable LAN console (control port pool, 9295 observed).</summary>
public sealed class HalyardTcpControlChannel : IHalyardControlChannel, IHalyardRpcsTransport
{
    private readonly byte[] _receiveBuffer = new byte[16 * 1024];
    private readonly List<byte> _pending = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpByteChannel? _channel;

    /// <summary>
    /// Open (or re-open) the control connection. Safe to call more than once per session: the console serves
    /// <c>/sess/init</c> with <c>Connection: close</c> and then <c>/sess/ctrl</c> on a <em>fresh</em>
    /// connection (wire-confirmed), so the session reconnects between them. Any prior connection is disposed
    /// and the receive buffer cleared so the new connection starts clean.
    /// </summary>
    public async Task ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
            _channel = null;
        }

        _pending.Clear();
        _channel = await TcpByteChannel.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessResponse> SendRequestAsync(SessRequest request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await _channel!.SendAsync(request.Serialize(), cancellationToken).ConfigureAwait(false);

        while (true)
        {
            if (SessResponse.TryParse(_pending.ToArray(), out SessResponse? response, out int consumed) && response is not null)
            {
                _pending.RemoveRange(0, consumed);
                return response;
            }

            await FillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SendCtrlMessageAsync(HalyardCtrlMessage message, CancellationToken cancellationToken)
    {
        EnsureConnected();
        // Reads and writes on the underlying stream run concurrently (a background reader + heartbeat
        // replies); serialize writes so two senders can't interleave a frame on the wire.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel!.SendAsync(message.Serialize(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<HalyardCtrlMessage?> ReadCtrlMessageAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        while (true)
        {
            if (HalyardCtrlMessage.TryParse(_pending.ToArray(), out HalyardCtrlMessage message, out int consumed))
            {
                _pending.RemoveRange(0, consumed);
                return message;
            }

            int read = await FillAsync(cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null; // channel closed
            }
        }
    }

    public async Task SendRpcsFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        EnsureConnected();
        // Share the write lock with the binary ctrl path so RPCS frames and any other writer never interleave.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel!.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<byte[]?> ReadRpcsFrameAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        while (true)
        {
            if (_pending.Count >= HalyardRpcsChannel.HeaderLength)
            {
                byte[] header = [.. _pending.GetRange(0, HalyardRpcsChannel.HeaderLength)];
                if (!HalyardRpcsChannel.TryGetFrameLength(header, out int frameLength))
                    throw new InvalidDataException("Non-RPCS data on the control channel after /sess/ctrl.");
                if (_pending.Count >= frameLength)
                {
                    byte[] frame = [.. _pending.GetRange(0, frameLength)];
                    _pending.RemoveRange(0, frameLength);
                    return frame;
                }
            }

            int read = await FillAsync(cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null; // channel closed
            }
        }
    }

    private async Task<int> FillAsync(CancellationToken cancellationToken)
    {
        int read = await _channel!.ReceiveAsync(_receiveBuffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _pending.AddRange(_receiveBuffer.AsSpan(0, read));
        }

        return read;
    }

    private void EnsureConnected()
    {
        if (_channel is null)
        {
            throw new InvalidOperationException("Control channel not connected.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        return _channel?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
