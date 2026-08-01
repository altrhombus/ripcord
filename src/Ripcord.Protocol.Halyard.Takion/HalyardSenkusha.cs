using System.Diagnostics;
using System.Net;
using Avstream;
using Google.Protobuf;
using Ripcord.Core.Net.Udp;

namespace Ripcord.Protocol.Halyard.Takion;

/// <summary>
/// What the senkusha bring-up learned about the link.
///
/// <para>
/// <see cref="RoundTripTimeMs"/> is null when nothing could be measured, which is deliberately distinct from 0 —
/// the launchSpec previously declared <c>rtt: 0</c> unconditionally, asserting a measurement that had never been
/// taken.
/// </para>
/// </summary>
public sealed record SenkushaResult(bool Succeeded, double? RoundTripTimeMs, int? ConfirmedMtu = null)
{
    public static SenkushaResult Failed { get; } = new(false, null);
}

/// <summary>
/// The v1 "senkusha" bring-up: a short-lived, keyless Takion connection on the console's senkusha port
/// (UDP 9297) that the console requires between <c>/sess/ctrl</c> and the A/V stream. The vendor runs it
/// before opening the stream on 9296; skipping it leaves the stream's SESSION exchange unanswered.
///
/// <para>
/// This implements the minimal bring-up wire-confirmed against a full vendor session (cap22): Takion
/// connection handshake → <c>PROTOCOL_VERSION_REQUEST{[9]}</c> (channel 0x15) → keyless
/// <c>SESSION_REQUEST</c> (empty sessionKey/launchSpec/encryptedKey, clientVersion 9; channel 0x01) →
/// <c>SESSION_REPLY</c> → <c>DISCONNECT</c>. The full senkusha additionally runs RTT (echo) and MTU probes
/// on channel 0x08 to measure the link; those feed only tuning defaults and are added later if the console
/// turns out to require them.
/// </para>
///
/// <para>
/// <b>Provenance.</b> The probe sequence, packet layout and field meanings are <b>[W]</b> — decoded from our own
/// capture <c>session8-wireshark.pcapng</c>, frames 154-189, with frame numbers cited at each step. The decision to
/// treat probe failure as NON-FATAL is a design choice of ours (fall back to MTU 1454). It is supported by the
/// capture — the
/// vendor's own bring-up proceeds regardless — and by the plain engineering argument that a tuning probe must never
/// be the reason a stream fails to start.
/// </para>
/// </summary>
public sealed class HalyardSenkusha : IAsyncDisposable
{
    /// <summary>The Takion/SESSION_REQUEST version senkusha negotiates (wire-confirmed: supportedVersions=[9], clientVersion=9).</summary>
    public const uint SenkushaVersion = 9;

    /// <summary>Whole-probe budget. Ten pings on a LAN take a few milliseconds; this is a backstop, not a target.</summary>
    private static readonly TimeSpan EchoProbeBudget = TimeSpan.FromSeconds(3);

    /// <summary>Per-ping wait. Generous for a bad Wi-Fi link, short enough that ten misses cost under a second.</summary>
    private static readonly TimeSpan EchoReplyTimeout = TimeSpan.FromMilliseconds(80);

    /// <summary>Whole-MTU-probe budget, covering both directions.</summary>
    private static readonly TimeSpan MtuProbeBudget = TimeSpan.FromSeconds(3);

    /// <summary>Wait for a probe reply. Longer than a ping: the console does real work between request and reply.</summary>
    private static readonly TimeSpan MtuReplyTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>Downstream probes requested. The vendor asks for one, and one arriving is the whole result.</summary>
    private const uint MtuProbeCount = 1;

    private readonly UdpChannel _socket;
    private readonly IPEndPoint _console;

    // Sequence numbers of echoed pings, handed from the receive loop to the probe. Unbounded but tiny: ten pings.
    private readonly System.Threading.Channels.Channel<byte> _echoes =
        System.Threading.Channels.Channel.CreateUnbounded<byte>();

    private long _mtuProbesReceived;

    private TakionReliableChannel? _reliable;
    private CancellationTokenSource? _loopCts;
    private Task? _receiveLoop;

    public HalyardSenkusha(UdpChannel socket, IPEndPoint console)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    /// <summary>
    /// Run the senkusha bring-up, timing its exchanges. Non-fatal by design: the caller proceeds to the stream
    /// regardless (matching the vendor client, which tolerates senkusha failures).
    ///
    /// <para>
    /// RTT comes from the two request/reply round trips this bring-up already performs, so it costs no extra wire
    /// traffic and cannot disturb a handshake the console is known to accept. That matters more than it sounds: the
    /// full vendor probe suite (ECHO to enable echoing, then MTU and BANDWIDTH commands on channel 0x08) would have
    /// to be written blind against a working handshake, and getting it wrong breaks the one thing that must succeed
    /// for the stream to start at all. Bandwidth measurement is therefore still outstanding — see the class remarks.
    /// </para>
    /// </summary>
    /// <param name="candidateMtu">
    /// The MTU to verify, normally the interface-derived estimate. The probe confirms or refutes it rather than
    /// searching: the vendor tests exactly one size, and a binary search would be our invention, not observed
    /// behaviour.
    /// </param>
    public async Task<SenkushaResult> RunAsync(
        TimeSpan handshakeTimeout,
        int handshakeAttempts,
        int candidateMtu,
        CancellationToken cancellationToken)
    {
        var connection = new TakionConnection(_socket, _console);
        await connection.ConnectAsync(handshakeTimeout, handshakeAttempts, cancellationToken).ConfigureAwait(false);

        _reliable = new TakionReliableChannel(_socket, _console, connection.LocalTag, connection.RemoteTag);
        _reliable.StartFed();

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_loopCts.Token));

        // Protocol-version negotiation (channel 0x15).
        var protocolRequest = new ControlMessage
        {
            Type = ControlMessage.Types.MessageType.ProtocolVersionRequest,
            ProtocolVersionRequest = new ProtocolVersionRequestPayload { SupportedVersions = { SenkushaVersion } },
        };
        // Timed: this exchange is the purest RTT sample available, because answering a version handshake asks the
        // console to do essentially no work.
        long versionSentAt = Stopwatch.GetTimestamp();
        await _reliable.SendMessageAsync(TakionDataChunk.ChannelProtocolVersion, protocolRequest, cancellationToken).ConfigureAwait(false);
        if (await ReceiveUntilAsync(ControlMessage.Types.MessageType.ProtocolVersionAck, cancellationToken).ConfigureAwait(false) is null)
        {
            return SenkushaResult.Failed;
        }

        var samples = new List<double> { Stopwatch.GetElapsedTime(versionSentAt).TotalMilliseconds };

        // Keyless SESSION_REQUEST (empty session key / launch spec / encrypted key) -> SESSION_REPLY (channel 0x01).
        var big = new ControlMessage
        {
            Type = ControlMessage.Types.MessageType.SessionRequest,
            SessionRequestPayload = new SessionRequestPayload
            {
                ClientVersion = SenkushaVersion,
                SessionKey = string.Empty,
                LaunchSpecJson = string.Empty,
                EncryptedKey = ByteString.Empty,
            },
        };
        long requestSentAt = Stopwatch.GetTimestamp();
        await _reliable.SendMessageAsync(TakionDataChunk.ChannelSession, big, cancellationToken).ConfigureAwait(false);
        if (await ReceiveUntilAsync(ControlMessage.Types.MessageType.SessionReply, cancellationToken).ConfigureAwait(false) is null)
        {
            return SenkushaResult.Failed;
        }

        // Included even though a session reply involves real console-side work: RoundTripToDeclare takes the
        // MINIMUM, so a slow sample can only be ignored, never inflate the result.
        samples.Add(Stopwatch.GetElapsedTime(requestSentAt).TotalMilliseconds);

        // Echo probe: a real RTT measurement, replacing the estimate derived from handshake round trips. Ordered
        // exactly as the capture shows — after SESSION_REPLY, before DISCONNECT.
        double? echoRtt = await RunEchoProbeAsync(cancellationToken).ConfigureAwait(false);
        if (echoRtt is double measured)
        {
            samples.Clear();
            samples.Add(measured);
        }

        // MTU probes, in the capture's order: downstream first, then upstream. Both are best-effort — a null result
        // just means the launchSpec keeps the interface-derived estimate.
        int? confirmedMtu = await RunMtuProbeAsync(candidateMtu, cancellationToken).ConfigureAwait(false);

        // Politely disconnect the senkusha connection (best-effort; the console tolerates a dropped one too).
        try
        {
            var disconnect = new ControlMessage
            {
                Type = ControlMessage.Types.MessageType.Disconnect,
                DisconnectPayload = new DisconnectPayload { Reason = "Client Disconnecting" },
            };
            await _reliable.SendMessageAsync(TakionDataChunk.ChannelSession, disconnect, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ignore — the bring-up already succeeded
        }

        return new SenkushaResult(true, LinkMetrics.RoundTripToDeclare(samples), confirmedMtu);
    }

    /// <summary>
    /// Measure RTT with the vendor's echo probe: enable echoing, send ten pings, time each round trip, disable it.
    ///
    /// <para>
    /// Strictly best-effort and bounded. Returns null on any failure or timeout, in which case the caller keeps the
    /// RTT it derived from the handshake exchanges — a worse measurement, but never worse than no session. The whole
    /// point of senkusha is to be tolerated when it goes wrong: the vendor client tolerates failures here, and this
    /// probe must not be the reason a stream fails to start.
    /// </para>
    /// </summary>
    private async Task<double?> RunEchoProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(EchoProbeBudget);
            CancellationToken token = probeCts.Token;

            await SendEchoCommandAsync(enabled: true, token).ConfigureAwait(false);

            var samples = new List<double>(SenkushaEchoProbe.PingCount);
            for (byte sequence = 0; sequence < SenkushaEchoProbe.PingCount; sequence++)
            {
                long microseconds = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1_000_000);
                byte[] ping = SenkushaEchoProbe.Build(sequence, microseconds);

                long sentAt = Stopwatch.GetTimestamp();
                await _socket.SendAsync(ping, _console, token).ConfigureAwait(false);

                if (await WaitForEchoAsync(sequence, token).ConfigureAwait(false))
                {
                    samples.Add(Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds);
                }
            }

            await SendEchoCommandAsync(enabled: false, token).ConfigureAwait(false);

            // Needs a majority to be trustworthy: one stray echo out of ten says more about luck than the link.
            return samples.Count * 2 >= SenkushaEchoProbe.PingCount
                ? LinkMetrics.RoundTripToDeclare(samples)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Await the echo of one specific ping, ignoring any that arrive out of order.</summary>
    private async Task<bool> WaitForEchoAsync(byte sequence, CancellationToken cancellationToken)
    {
        using var perPing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perPing.CancelAfter(EchoReplyTimeout);

        try
        {
            while (await _echoes.Reader.WaitToReadAsync(perPing.Token).ConfigureAwait(false))
            {
                while (_echoes.Reader.TryRead(out byte echoed))
                {
                    if (echoed == sequence)
                    {
                        return true;
                    }

                    // A late echo of an earlier ping: drop it rather than crediting it to this one, which would
                    // report a round trip shorter than it was.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // No echo for this ping within its window; the caller simply gets one fewer sample.
        }

        return false;
    }

    private Task SendEchoCommandAsync(bool enabled, CancellationToken cancellationToken)
    {
        var message = new ControlMessage
        {
            Type = ControlMessage.Types.MessageType.BandwidthProbe,
            BandwidthProbePayload = new Avstream.Bandwidth.BandwidthProbePayload
            {
                Command = Avstream.Bandwidth.BandwidthProbePayload.Types.Command.EchoCommand,
                EchoCommand = new Avstream.Bandwidth.EchoCommand { State = enabled },
            },
        };

        return _reliable!.SendMessageAsync(TakionDataChunk.ChannelBandwidth, message, cancellationToken);
    }

    /// <summary>
    /// Verify an MTU in both directions, exactly as the captured vendor session does.
    ///
    /// <para>
    /// DOWNSTREAM (capture frames 177-181): we send <c>MTU_COMMAND{id, mtuReq, num}</c>, the console sends that many
    /// datagrams of that size, then replies with <c>mtuSent</c>. Arrival is the entire result — a datagram of the
    /// requested size reaching us intact is what "this MTU works" means, so the contents are never examined.
    /// </para>
    ///
    /// <para>
    /// UPSTREAM (frames 183-189): <c>CLIENT_MTU_COMMAND{state=true}</c> puts the console in echo mode for one large
    /// packet, we send it in the echo format at the requested size, and the console returns it. Then
    /// <c>CLIENT_MTU_COMMAND{state=false}</c> closes the test. The vendor tests a smaller size upstream than
    /// downstream (1254 vs 1454), which is consistent with asymmetric links being the norm.
    /// </para>
    ///
    /// <para>
    /// Returns the confirmed MTU, or null if either direction could not be verified — in which case the caller keeps
    /// the interface-derived estimate. Declaring an unverified figure is the status quo; declaring one the probe just
    /// showed to be too large would be worse than not probing at all.
    /// </para>
    /// </summary>
    private async Task<int?> RunMtuProbeAsync(int candidateMtu, CancellationToken cancellationToken)
    {
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(MtuProbeBudget);
            CancellationToken token = probeCts.Token;

            if (!await ProbeDownstreamMtuAsync(candidateMtu, token).ConfigureAwait(false))
            {
                return null;
            }

            // Upstream is tested at the same size we intend to declare. The vendor used a smaller one, but it also
            // had a reason to know its own uplink; we do not, so verifying the value we will actually send is more
            // useful than reproducing their number.
            return await ProbeUpstreamMtuAsync(candidateMtu, token).ConfigureAwait(false)
                ? candidateMtu
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<bool> ProbeDownstreamMtuAsync(int mtu, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _mtuProbesReceived, 0);

        await SendProbeAsync(
            new Avstream.Bandwidth.BandwidthProbePayload
            {
                Command = Avstream.Bandwidth.BandwidthProbePayload.Types.Command.MtuCommand,
                MtuCommand = new Avstream.Bandwidth.MtuCommand
                {
                    Id = 1,
                    MtuReq = (uint)mtu,
                    Num = MtuProbeCount,
                },
            },
            cancellationToken).ConfigureAwait(false);

        // The console's reply is the end of the test, not the evidence: it reports what it SENT, while what matters
        // is what arrived. Awaited so the exchange completes tidily before the upstream half begins.
        await ReceiveProbeReplyAsync(
            Avstream.Bandwidth.BandwidthProbePayload.Types.Command.MtuCommand, cancellationToken).ConfigureAwait(false);

        return Interlocked.Read(ref _mtuProbesReceived) > 0;
    }

    private async Task<bool> ProbeUpstreamMtuAsync(int mtu, CancellationToken cancellationToken)
    {
        int payloadLength = SenkushaEchoProbe.PayloadForMtu(mtu);
        if (payloadLength <= 0)
        {
            return false;
        }

        await SendProbeAsync(ClientMtu(id: 1, mtu, state: true), cancellationToken).ConfigureAwait(false);
        await ReceiveProbeReplyAsync(
            Avstream.Bandwidth.BandwidthProbePayload.Types.Command.ClientMtuCommand, cancellationToken)
            .ConfigureAwait(false);

        // Sequence 0: this is a fresh single-packet test, not a continuation of the ping run.
        long micros = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1_000_000);
        // Padded with the vendor's fill byte, not zeros: a zero-filled payload is compressible, and a link that
        // compresses it would let this test pass at a size the path cannot really carry.
        byte[] probe = SenkushaEchoProbe.Build(
            0, micros, payloadLength, SenkushaEchoProbe.MtuPaddingByte);

        await _socket.SendAsync(probe, _console, cancellationToken).ConfigureAwait(false);

        bool echoed = await WaitForEchoAsync(0, cancellationToken).ConfigureAwait(false);

        // Close the test either way: leaving the console in client-MTU mode is a state we have no way to clear later.
        await SendProbeAsync(ClientMtu(id: 2, mtu, state: false), cancellationToken).ConfigureAwait(false);
        return echoed;
    }

    private static Avstream.Bandwidth.BandwidthProbePayload ClientMtu(uint id, int mtu, bool state)
        => new()
        {
            Command = Avstream.Bandwidth.BandwidthProbePayload.Types.Command.ClientMtuCommand,
            ClientMtuCommand = new Avstream.Bandwidth.ClientMtuCommand
            {
                Id = id,
                MtuReq = (uint)mtu,
                State = state,
                MtuDown = (uint)mtu,
            },
        };

    private Task SendProbeAsync(
        Avstream.Bandwidth.BandwidthProbePayload payload, CancellationToken cancellationToken)
        => _reliable!.SendMessageAsync(
            TakionDataChunk.ChannelBandwidth,
            new ControlMessage
            {
                Type = ControlMessage.Types.MessageType.BandwidthProbe,
                BandwidthProbePayload = payload,
            },
            cancellationToken);

    /// <summary>
    /// Await a BANDWIDTH_PROBE reply carrying a specific command, ignoring other probe traffic.
    ///
    /// <para>
    /// Filtering on the command matters because every probe reply shares one message type: without it, the reply to
    /// the downstream test would satisfy a wait intended for the upstream one.
    /// </para>
    /// </summary>
    private async Task<bool> ReceiveProbeReplyAsync(
        Avstream.Bandwidth.BandwidthProbePayload.Types.Command command, CancellationToken cancellationToken)
    {
        using var perReply = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perReply.CancelAfter(MtuReplyTimeout);

        try
        {
            while (!perReply.Token.IsCancellationRequested)
            {
                ControlMessage message = await _reliable!
                    .ReceiveMessageAsync(perReply.Token).ConfigureAwait(false);

                if (message.Type == ControlMessage.Types.MessageType.BandwidthProbe
                    && message.BandwidthProbePayload?.Command == command)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // No reply in the window. Non-fatal: the caller falls back to the unverified estimate.
        }

        return false;
    }

    private async Task<ControlMessage?> ReceiveUntilAsync(ControlMessage.Types.MessageType type, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ControlMessage message;
            try
            {
                message = await _reliable!.ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (message.Type == type)
            {
                return message;
            }
        }

        return null;
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

                byte baseType = (byte)(result.Buffer[0] & 0x0f);
                if (baseType == TakionMessageHeader.BaseTypeControl)
                {
                    await _reliable!.HandlePacketAsync(result.Buffer, cancellationToken).ConfigureAwait(false);
                }
                else if (SenkushaEchoProbe.TryReadEcho(result.Buffer, out byte sequence))
                {
                    // Echoed ping. This loop used to discard every non-control packet, which is exactly the traffic
                    // the RTT probe depends on.
                    _echoes.Writer.TryWrite(sequence);
                }
                else if (baseType == TakionMessageHeader.BaseTypeSenkushaMtu)
                {
                    // The console's downstream MTU probe. Only the ARRIVAL matters: a datagram of that size reaching
                    // us intact is the whole result, so the contents are never inspected.
                    Interlocked.Increment(ref _mtuProbesReceived);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { /* ignore shutdown races */ }
        }

        if (_reliable is not null)
        {
            await _reliable.DisposeAsync().ConfigureAwait(false);
        }

        _loopCts?.Dispose();
    }
}
