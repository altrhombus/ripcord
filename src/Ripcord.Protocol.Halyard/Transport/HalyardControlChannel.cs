using System.Net;
using Ripcord.Core.Net.Tcp;
using Ripcord.Protocol.Halyard.Common.Control;

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

/// <summary>Plain-TCP control channel for a directly-reachable LAN console (control port pool, 9295 observed).</summary>
public sealed class HalyardTcpControlChannel : IHalyardControlChannel
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
