using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Ripcord.Protocol.Halyard.Transport;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// Drives the v2 RPCS during-stream control channel over the connection that carried <c>/sess/ctrl</c> (spec §9).
/// It keeps the channel alive — a periodic keepalive (<c>0x0036</c>) and, once at start, the probe report
/// (<c>0x000d</c>, the v2 analogue of v1's pre-stream probe) — while reading incoming frames and surfacing each
/// decoded message through <see cref="MessageReceived"/> for the session to act on (session-id, stream-channel
/// setup, errors). Teardown (<c>0x00fe</c>) or a closed connection ends the loop.
///
/// <para>The <b>framing, commands and payloads are confirmed</b> from our own capture (session 004); the
/// <b>send schedule is <c>[X]</c></b> — whether the probe report is required, its ordering, and the keepalive
/// cadence were not pinned offline. The defaults here (probe once up front, keepalive every second) are a
/// first-guess from the confirmed commands and the v1 analogue, to be settled on hardware.</para>
/// </summary>
internal sealed class HalyardRpcsLoop
{
    private readonly IHalyardRpcsTransport _transport;
    private readonly HalyardRpcsChannel _channel;
    private readonly TimeSpan _keepaliveInterval;
    private readonly ushort _mtu;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>Raised for each decoded incoming RPCS message (on the reader task). Handlers must not block.</summary>
    public event Action<HalyardRpcsMessage>? MessageReceived;

    public HalyardRpcsLoop(
        IHalyardRpcsTransport transport,
        HalyardRpcsChannel channel,
        TimeSpan? keepaliveInterval = null,
        ushort mtu = HalyardRpcsCommands.DefaultMtu)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _keepaliveInterval = keepaliveInterval ?? TimeSpan.FromSeconds(1); // [X] cadence
        _mtu = mtu;
    }

    /// <summary>
    /// Run until the connection closes, the console sends teardown, or <paramref name="cancellationToken"/> fires.
    /// Sends the probe report once, then runs the reader and the periodic keepalive together; when either ends,
    /// the other is stopped.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = linked.Token;

        // [X] The probe report up front — v1 required an equivalent before the console declared the stream ready.
        await SendAsync(HalyardRpcsCommands.ProbeReport, HalyardRpcsCommands.BuildProbeReportPayload(_mtu), token)
            .ConfigureAwait(false);

        Task keepalive = KeepaliveLoopAsync(token);
        try
        {
            await ReaderLoopAsync(token).ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();                       // stop the keepalive loop
            await Swallow(keepalive).ConfigureAwait(false);
        }
    }

    /// <summary>Seal and send one RPCS message. Serialized so the keepalive and any session send never race the
    /// channel's send counter.</summary>
    public async Task SendAsync(ushort cmd, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] frame = _channel.SealMessage(cmd, payload.Span);
            await _transport.SendRpcsFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReaderLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            byte[]? frame = await _transport.ReadRpcsFrameAsync(token).ConfigureAwait(false);
            if (frame is null)
                return; // connection closed

            HalyardRpcsMessage? decoded = _channel.OpenMessage(frame);
            if (decoded is not { } message)
                continue; // a frame that didn't authenticate/decode — drop it, keep the channel

            MessageReceived?.Invoke(message);
            if (message.Cmd == HalyardRpcsCommands.Teardown)
                return;
        }
    }

    private async Task KeepaliveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_keepaliveInterval, token).ConfigureAwait(false);
                await SendAsync(HalyardRpcsCommands.Keepalive, HalyardRpcsCommands.KeepalivePayload.ToArray(), token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop: the reader ended and cancelled us.
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the linked token cancels the keepalive loop.
        }
    }
}
