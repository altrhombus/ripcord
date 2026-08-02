using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Avstream;
using Ripcord.Core.Net.Udp;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Streaming;

namespace Ripcord.Protocol.Halyard.Takion;

/// <summary>
/// Orchestrates the full v1 stream over one UDP socket: the Takion connection handshake, then a single
/// receive loop that demultiplexes by base-type - control packets (base-type 0x00) drive the reliable
/// control channel, A/V packets (0x02 video / 0x03 audio) go to the stream demuxer (which authenticates +
/// decrypts them through the crypto seam). The SESSION_REQUEST/REPLY exchange runs over the reliable
/// channel to establish the stream keys.
///
/// This is the piece that joins the transport (Takion) to the crypto seam and the media demuxer - the
/// "first picture" path. The caller supplies the already-built (out1-encrypted) launchSpec + handshakeKey.
/// </summary>
public sealed class HalyardTakionStream : IAsyncDisposable
{
    private readonly UdpChannel _socket;
    private readonly IPEndPoint _console;
    private readonly IHalyardSessionCrypto _crypto;
    private readonly HalyardStreamDemuxer _demuxer;

    private TakionReliableChannel? _reliable;
    private CancellationTokenSource? _loopCts;
    private Task? _receiveLoop;
    private Task? _avProcessorLoop;
    private Task? _controlLoop;
    private Task? _congestionLoop;

    // A/V datagrams are handed from the receive thread to a dedicated processing thread through this bounded
    // queue, so the socket keeps draining at line rate even when decrypt/FEC/reassembly/decode is momentarily
    // slow (a compute spike, a GC pause, a decode stall). Bounded + drop-oldest is the resilience policy:
    // under sustained overload we shed the OLDEST datagrams to stay near-live (bounded added latency) instead
    // of bloating the socket buffer for seconds; FEC and the IDR-request path recover the picture. Control
    // packets are NOT queued here — they stay inline on the receive loop (low volume, must remain reliable and
    // in order: SACKs, keepalives, STREAM_INFO).
    // Bound on queued A/V datagrams before the oldest are shed.
    //
    // The previous comment claimed "~0.5 s of 720p60 headroom", which was both a guess and stated in units that no
    // longer apply (we run 1080p, and a datagram is not a frame — one frame spans many). What the number actually
    // guarantees is a bound: at most this many datagrams are held, so a processing stall costs bounded memory and
    // bounded added latency rather than unbounded growth. In normal operation depth sits at 0, so this is a safety
    // valve, not a tuning knob. Raise it only with evidence that shedding is happening.
    //
    // A depth AT capacity is therefore a diagnosis, not a tuning signal: it means the A/V processing loop is slower
    // than the wire, and the "packet loss" the demuxer then reports is ours, not the network's. It has read that way
    // twice, both times from per-packet crypto cost — pegged at 511 before the x86 GHASH fix, and again at 512 on
    // the ARM64 first run, which had no carry-less GHASH path at all (see AesGcmCore.GfMul). Check the crypto hot
    // path before believing the link.
    private const int AvQueueCapacity = 512;
    private Channel<byte[]>? _avQueue;

    /// <summary>
    /// Current depth of the A/V receive queue (datagrams waiting for the processing thread). Normally ~0; a
    /// depth climbing toward <see cref="AvQueueCapacity"/> means the processor is falling behind and the
    /// DropOldest policy is (about to be) shedding — i.e. a loss blip that originates in US, not on the wire.
    /// </summary>
    public int AvQueueDepth => _avQueue is { Reader: { CanCount: true } reader } ? reader.Count : 0;

    /// <summary>
    /// Smoothed network round-trip time in milliseconds, measured from control-DATA → SACK on the reliable
    /// channel; 0 until the first clean sample. Surfaced so the session can report it and the health assessor
    /// can tell the user "the picture is fine, the link is slow" — previously hardcoded to 0 and never measured.
    /// </summary>
    public double RoundTripTimeMs => _reliable?.RoundTripTimeMs ?? 0;

    private long _lastConsoleActivityTicks;

    /// <summary>
    /// Milliseconds since anything at all arrived from the console, or null if nothing ever has.
    ///
    /// <para>
    /// Distinct from "since the last video frame", and the distinction matters: a completely static scene gives the
    /// encoder nothing to send, so frames legitimately stop while the console is perfectly healthy and still
    /// talking. Treating that as a stall would tear down a working session for showing a paused game.
    /// </para>
    /// </summary>
    public double? MillisecondsSinceConsoleActivity
    {
        get
        {
            long ticks = Volatile.Read(ref _lastConsoleActivityTicks);
            return ticks == 0 ? null : (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerMillisecond;
        }
    }

    /// <summary>Clean RTT samples folded into the estimate; 0 means the metric is not being fed.</summary>
    public long RoundTripSampleCount => _reliable?.RoundTripSampleCount ?? 0;

    // Wire throughput. Bytes are accumulated by the receive loop and converted to a rate by the congestion
    // loop, which already ticks on a fixed interval.
    private long _receivedAvBytes;
    private long _lastBitrateSampleTicks;
    private int _measuredBitrateKbps;

    /// <summary>
    /// Measured incoming A/V bitrate in kbps, averaged over the last congestion window; 0 before the first
    /// sample. This is the actual rate on the wire — distinct from the bitrate we *requested*, which the console
    /// is free to ignore and which the adaptive controller may have since lowered.
    /// </summary>
    public int MeasuredBitrateKbps => Volatile.Read(ref _measuredBitrateKbps);

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    // Congestion feedback: a raw sealed Takion packet (base type 5, 15 bytes) carrying received/lost unit
    // counts, sent periodically so the console's rate controller can adapt the encoder bitrate to the link.
    private static readonly TimeSpan CongestionInterval = TimeSpan.FromMilliseconds(200);
    private const byte CongestionPacketType = 0x05;
    private const int CongestionPacketSize = 15;

    /// <summary>Minimum spacing between IDR requests — one recovers the whole reference chain, so requesting
    /// on every lost slice while a burst is still arriving would just waste upstream bandwidth.</summary>
    private const long IdrRequestThrottleMs = 200;
    private long _lastIdrRequestTick;

    /// <summary>
    /// Raised once per <see cref="CongestionInterval"/> with the (received, lost) A/V unit counts for that
    /// window — the same numbers reported to the console's rate controller. Drives the on-screen loss readout
    /// (and, later, client-side bitrate adaptation). Counts are per-window (reset each tick).
    /// </summary>
    public event Action<long, long>? PacketStatsSampled;

    public HalyardTakionStream(UdpChannel socket, IPEndPoint console, IHalyardSessionCrypto crypto, HalyardStreamDemuxer demuxer)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _demuxer = demuxer ?? throw new ArgumentNullException(nameof(demuxer));
    }

    public bool IsStreamEstablished => _crypto.IsStreamEstablished;

    /// <summary>
    /// Run the handshake, start the demultiplexing receive loop, and negotiate the stream keys. Returns
    /// true when the stream crypto is established (A/V can then be decrypted). Throws
    /// <see cref="TimeoutException"/> if the Takion handshake is not answered.
    /// </summary>
    public async Task<TakionSessionResult> StartAsync(
        TakionSessionRequest request,
        TimeSpan handshakeTimeout,
        int handshakeAttempts,
        CancellationToken cancellationToken)
    {
        // 1. Takion connection handshake (INIT/INIT_ACK/COOKIE_ECHO).
        var connection = new TakionConnection(_socket, _console);
        await connection.ConnectAsync(handshakeTimeout, handshakeAttempts, cancellationToken).ConfigureAwait(false);

        // 2. Reliable control channel in fed mode - the loop below routes control packets to it.
        _reliable = new TakionReliableChannel(_socket, _console, connection.LocalTag, connection.RemoteTag);
        _reliable.StartFed();

        // 3. Receive loop (reads the socket, demultiplexes control vs A/V) plus a dedicated A/V processing loop
        //    that drains the bounded queue. Keeping the expensive A/V work off the receive thread is what lets
        //    the socket drain at line rate under load (see AvQueueCapacity).
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _avQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(AvQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // shed oldest under overload to stay near-live
            SingleReader = true,
            SingleWriter = true,
        });
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_loopCts.Token));
        _avProcessorLoop = Task.Run(() => AvProcessorLoopAsync(_loopCts.Token));

        // 4. Stream key agreement over the reliable channel.
        var negotiator = new TakionSessionNegotiator(_reliable, _crypto);
        TakionSessionResult result = await negotiator.NegotiateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return result;
        }

        // 5. Now that the stream keys are established, control DATA must be GMAC-authenticated or the console
        //    drops it. Enable sealing before sending anything further (STREAM_INFO_ACK, heartbeats).
        _reliable.EnableSealing(packet => _crypto.SealControlMessage(packet));

        // When the demuxer sees lost slices, tell the console (CORRUPT_FRAME) and ask for a fresh IDR so the
        // picture snaps back instead of the reference chain degrading over seconds. Sealed, so wire it after
        // sealing is on.
        _demuxer.VideoLossDetected += OnVideoLossDetected;

        // 6. Post-key-agreement: the console sends STREAM_INFO and will NOT start A/V until we ack it (it
        //    otherwise disconnects with "streaminfoack fail"). Ack it, then keep the session alive with
        //    periodic heartbeats while the receive loop feeds A/V to the demuxer.
        await AckStreamInfoAsync(cancellationToken).ConfigureAwait(false);
        _controlLoop = Task.Run(() => StreamControlLoopAsync(_loopCts.Token));
        _congestionLoop = Task.Run(() => CongestionLoopAsync(_loopCts.Token));
        return result;
    }

    /// <summary>
    /// Convert the bytes accumulated since the previous sample into a kbps figure. Called on the congestion
    /// loop's tick, so the window is that loop's interval; the elapsed time is measured rather than assumed, so
    /// a late tick under load reports the correct rate instead of an inflated one.
    /// </summary>
    private void SampleMeasuredBitrate()
    {
        long bytes = Interlocked.Exchange(ref _receivedAvBytes, 0);
        long now = Stopwatch.GetTimestamp();
        long previous = _lastBitrateSampleTicks;
        _lastBitrateSampleTicks = now;

        if (previous == 0)
        {
            return; // first tick establishes the baseline only
        }

        double seconds = (now - previous) / (double)Stopwatch.Frequency;
        if (seconds <= 0)
        {
            return;
        }

        Volatile.Write(ref _measuredBitrateKbps, (int)Math.Round(bytes * 8 / seconds / 1000.0));
    }

    /// <summary>Wait for the console's STREAM_INFO and reply STREAM_INFO_ACK (channel 9, bare message).</summary>
    private async Task AckStreamInfoAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ControlMessage message = await _reliable!.ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
            if (message.Type == ControlMessage.Types.MessageType.StreamInfo)
            {
                // STREAM_INFO carries the video parameter sets (SPS/PPS) per resolution; hand them to the
                // demuxer so it can prepend them to the first IDR (they are not sent in the video stream).
                var resolutions = message.StreamInfoPayload?.Resolution;
                if (resolutions is { Count: > 0 } && resolutions[0].VideoHeader is { Length: > 0 } vh)
                {
                    _demuxer.SetVideoHeader(vh.Span);
                }

                await SendStreamInfoAckAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            // ignore heartbeats etc. until STREAM_INFO arrives
        }
    }

    private Task SendStreamInfoAckAsync(CancellationToken cancellationToken)
        => _reliable!.SendMessageAsync(
            TakionDataChunk.ChannelStreamInfo,
            new ControlMessage { Type = ControlMessage.Types.MessageType.StreamInfoAck },
            cancellationToken);

    /// <summary>
    /// Keep the stream alive after the handshake: send periodic heartbeats (channel 1) and drain incoming
    /// control messages — re-acking STREAM_INFO if resent. A/V packets are handled by the receive loop.
    /// </summary>
    private async Task StreamControlLoopAsync(CancellationToken cancellationToken)
    {
        Task heartbeats = HeartbeatLoopAsync(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ControlMessage message = await _reliable!.ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
                switch (message.Type)
                {
                    case ControlMessage.Types.MessageType.StreamInfo:
                        await SendStreamInfoAckAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        break; // heartbeats, bandwidth, cursor, etc. — nothing required yet
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            try { await heartbeats.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    /// <summary>Demuxer loss callback (sync): fire-and-forget the feedback send on the loop's token.</summary>
    private void OnVideoLossDetected(int startFrame, int endFrame)
        => _ = SendVideoLossFeedbackAsync(startFrame, endFrame);

    private async Task SendVideoLossFeedbackAsync(int startFrame, int endFrame)
    {
        CancellationToken token = _loopCts?.Token ?? CancellationToken.None;
        try
        {
            await _reliable!.SendMessageAsync(
                TakionDataChunk.ChannelSession,
                new ControlMessage
                {
                    Type = ControlMessage.Types.MessageType.CorruptFrame,
                    CorruptPayload = new CorruptFramePayload { Start = (uint)startFrame, End = (uint)endFrame },
                },
                token).ConfigureAwait(false);

            await RequestIdrAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception)
        {
            // best-effort feedback — never let a feedback failure take down the receive path
        }
    }

    /// <summary>
    /// Ask the console for a fresh IDR, honouring <see cref="IdrRequestThrottleMs"/>. Fire-and-forget entry
    /// point for callers outside the receive loop — the decode pipeline calls this when its backlog blows out
    /// and it decides to resynchronise rather than grind through stale frames.
    /// </summary>
    public void RequestKeyFrame() => _ = RequestIdrAsync(_loopCts?.Token ?? CancellationToken.None);

    /// <summary>
    /// Report our measured link quality and the bitrate we would like, via CONNECTION_QUALITY (spec type 16).
    /// This is the client → server side of rate control: the console's encoder decides its own rate, and this is
    /// how it learns what we are actually seeing rather than inferring it from congestion counts alone.
    ///
    /// <para>
    /// <b>Unvalidated on the wire.</b> The <c>targetBitrate</c> field's units are not confirmed — kbps is sent
    /// here to match the launchSpec's <c>bwKbpsSent</c> convention, but bps is equally plausible, and being
    /// wrong by 1000x would make the console choose an absurd rate. That is why the caller gates this behind an
    /// opt-in setting rather than enabling it by default.
    /// </para>
    ///
    /// <para>Fire-and-forget: a failed report must never disturb the stream.</para>
    /// </summary>
    public void ReportConnectionQuality(ConnectionQualityReport report)
        => _ = SendConnectionQualityAsync(report, _loopCts?.Token ?? CancellationToken.None);

    private async Task SendConnectionQualityAsync(ConnectionQualityReport report, CancellationToken cancellationToken)
    {
        TakionReliableChannel? reliable = _reliable;
        if (reliable is null)
        {
            return;
        }

        try
        {
            await reliable.SendMessageAsync(
                TakionDataChunk.ChannelSession,
                new ControlMessage
                {
                    Type = ControlMessage.Types.MessageType.ConnectionQuality,
                    ConnectionQualityPayload = new ConnectionQualityPayload
                    {
                        TargetBitrate = (uint)Math.Max(0, report.TargetBitrateKbps),
                        Rtt = report.RoundTripTimeMs,
                        LossPercent = report.LossPercent,
                    },
                },
                cancellationToken).ConfigureAwait(false);

            Interlocked.Increment(ref _connectionQualityReportsSent);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception)
        {
            // best-effort telemetry to the console; never let it affect playback
        }
    }

    private long _connectionQualityReportsSent;

    /// <summary>How many CONNECTION_QUALITY reports have been sent — proves the path is live, or is not.</summary>
    public long ConnectionQualityReportsSent => Interlocked.Read(ref _connectionQualityReportsSent);

    /// <summary>Send an IDR request unless one was sent too recently. One IDR recovers the whole chain, so
    /// requesting per lost slice while a burst is still arriving would only waste uplink.</summary>
    private async Task RequestIdrAsync(CancellationToken cancellationToken)
    {
        long now = Environment.TickCount64;
        if (now - _lastIdrRequestTick < IdrRequestThrottleMs || _reliable is null)
        {
            return;
        }

        _lastIdrRequestTick = now;
        try
        {
            await _reliable.SendMessageAsync(
                TakionDataChunk.ChannelSession,
                new ControlMessage { Type = ControlMessage.Types.MessageType.IdrRequest },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    /// <summary>
    /// Every <see cref="CongestionInterval"/>, send a sealed congestion packet with the received/lost unit
    /// counts accumulated since the last send. Best-effort: the session tolerates missed reports.
    /// </summary>
    private async Task CongestionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(CongestionInterval, cancellationToken).ConfigureAwait(false);

                (long received, long lost) = _demuxer.TakePacketStats();
                SampleMeasuredBitrate();
                PacketStatsSampled?.Invoke(received, lost);
                var packet = new byte[CongestionPacketSize];
                packet[0] = CongestionPacketType;
                // packet[1..2] is 0 in all 474 congestion packets of cap47 [V]. Layout derived 2026-07-29
                // from that capture: received @3 (u16 BE, 2..351 per interval), lost @5 (u16 BE, 0 in every
                // packet of that zero-loss session — so its position is confirmed but its semantic is
                // inferred), GMAC @7, key position @11. See spec §3.
                //
                // Version caveat: this 15-byte form is what cap47 shows. Our older session8 capture shows a
                // 23-byte variant of the same base type (sequence @1, 90 kHz timestamp @3, GMAC @15, key
                // position @19), so the size is protocol-version-specific, not universal. We only ever
                // negotiate the version cap47 used; if a future firmware rejects these, that variant is why.
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), (ushort)Math.Min(received, ushort.MaxValue));
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(5), (ushort)Math.Min(lost, ushort.MaxValue));
                _crypto.SealCongestionPacket(packet);

                await _socket.SendAsync(packet, _console, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception)
        {
            // best-effort feedback
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                await _reliable!.SendMessageAsync(
                    TakionDataChunk.ChannelSession,
                    new ControlMessage { Type = ControlMessage.Types.MessageType.Heartbeat },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!result.RemoteEndPoint.Equals(_console) || result.Buffer.Length == 0)
                {
                    continue;
                }

                // Stamped for EVERY accepted datagram, whatever its type. This is the only signal that proves the
                // CONSOLE is still there: our own congestion loop is timer-driven and keeps publishing statistics
                // even if the far end has vanished, so statistics prove nothing about liveness.
                Volatile.Write(ref _lastConsoleActivityTicks, DateTime.UtcNow.Ticks);

                byte baseType = (byte)(result.Buffer[0] & 0x0f);
                if (baseType == TakionMessageHeader.BaseTypeControl)
                {
                    // Control is low-volume and must stay reliable/ordered — handle it inline (never dropped).
                    await _reliable!.HandlePacketAsync(result.Buffer, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Count bytes as they come off the wire, before any queueing or shedding, so the measured
                    // rate reflects what the console is actually sending rather than what we managed to process.
                    Interlocked.Add(ref _receivedAvBytes, result.Buffer.Length);

                    // A/V (0x02 video, 0x03 audio) and other media: hand off to the processing loop so this
                    // thread returns immediately to draining the socket. DropOldest bounds latency under load
                    // (it sheds the oldest queued datagram to make room). TryWrite returns false only once the
                    // channel is completed at shutdown, which the loop's cancellation already handles.
                    _avQueue!.Writer.TryWrite(result.Buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    /// Drains the A/V queue on its own thread: authenticate + decrypt each datagram through the crypto seam and
    /// reassemble frames (and decode audio, via the demuxer's events). Runs off the receive thread so a slow
    /// decode/decrypt/GC pause can never stall the socket drain. A single bad datagram is swallowed so the
    /// loop survives.
    /// </summary>
    private async Task AvProcessorLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (byte[] datagram in _avQueue!.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    _demuxer.Ingest(datagram);
                }
                catch (Exception)
                {
                    // one malformed/undecryptable datagram must not kill the processing loop
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>Send a controller-input (or other feedback) packet up the stream socket.</summary>
    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        => _socket.SendAsync(packet, _console, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _demuxer.VideoLossDetected -= OnVideoLossDetected;

        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { /* ignore shutdown races */ }
        }

        // Stop the A/V processor: complete the queue so ReadAllAsync ends, then await it.
        _avQueue?.Writer.TryComplete();
        if (_avProcessorLoop is not null)
        {
            try { await _avProcessorLoop.ConfigureAwait(false); } catch { /* ignore shutdown races */ }
        }

        if (_controlLoop is not null)
        {
            try { await _controlLoop.ConfigureAwait(false); } catch { /* ignore shutdown races */ }
        }

        if (_congestionLoop is not null)
        {
            try { await _congestionLoop.ConfigureAwait(false); } catch { /* ignore shutdown races */ }
        }

        if (_reliable is not null)
        {
            await _reliable.DisposeAsync().ConfigureAwait(false);
        }

        _loopCts?.Dispose();
    }
}
