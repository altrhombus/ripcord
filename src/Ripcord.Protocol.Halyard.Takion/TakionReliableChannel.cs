using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Avstream;
using Google.Protobuf;
using Ripcord.Core.Net.Udp;

namespace Ripcord.Protocol.Halyard.Takion;

/// <summary>
/// Reliable control-message delivery over an established Takion connection (spec §8). Runs on top of the
/// handshake's verification tags and initial sequence numbers:
/// <list type="bullet">
///   <item><description><b>Send</b> — serialize a <c>ControlMessage</c>, fragment it across DATA chunks
///   (ending bit on the last), assign consecutive TSNs starting at the local tag, transmit, and hold each in
///   a retransmit queue until it is SACKed.</description></item>
///   <item><description><b>Receive</b> — accept in-order DATA, cumulative-SACK it, reassemble fragments into
///   whole messages, and expose them; drop/re-SACK out-of-order (minimal in-order delivery, sufficient on a
///   clean LAN per §8.2).</description></item>
/// </list>
/// One task owns the socket receive loop; a light timer drives retransmission. The caller reads delivered
/// messages via <see cref="ReceiveMessageAsync"/>.
/// </summary>
public sealed class TakionReliableChannel : IAsyncDisposable
{
    private const int MaxPayloadPerChunk = 1000; // leaves room under a 1400-byte MTU for headers

    private readonly UdpChannel _channel;
    private readonly IPEndPoint _remote;
    private readonly uint _remoteTag;
    private readonly TimeSpan _retransmitInterval;

    private readonly TakionMessageReassembler _reassembler = new();
    private readonly Channel<ControlMessage> _inbound = System.Threading.Channels.Channel.CreateUnbounded<ControlMessage>();
    private readonly Lock _gate = new();
    private readonly SortedDictionary<uint, byte[]> _unacked = [];

    // RTT estimation. Send timestamp per outstanding TSN; a TSN that gets retransmitted is struck from the
    // set (Karn's algorithm) because we can no longer tell which transmission the SACK answers, and counting
    // it would inflate the estimate by a whole retransmit interval.
    private readonly Dictionary<uint, long> _sendTimestamps = [];
    private readonly HashSet<uint> _retransmitted = [];
    private double _smoothedRttMs;
    private long _rttSampleCount;

    private Action<byte[]>? _sealer; // seals outgoing control DATA (GMAC) once the stream keys are established
    private uint _nextSendTsn;   // next TSN to assign to an outgoing DATA chunk
    private uint _expectedRecvTsn; // next in-order TSN we expect from the peer
    private bool _haveExpected;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _retransmitLoop;

    public TakionReliableChannel(UdpChannel channel, IPEndPoint remote, uint localTag, uint remoteTag, TimeSpan? retransmitInterval = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _remoteTag = remoteTag;
        _nextSendTsn = localTag;      // the local initial TSN == the local verification tag
        _retransmitInterval = retransmitInterval ?? TimeSpan.FromMilliseconds(300);
    }

    /// <summary>
    /// Start the owned receive loop + retransmit loop (this channel reads the socket itself). Use when the
    /// channel has the socket to itself; call once, after the handshake is established.
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _retransmitLoop = Task.Run(() => RetransmitLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Start only the retransmit loop; the caller owns the socket and feeds control packets via
    /// <see cref="HandlePacketAsync"/> (used when a single receive loop demultiplexes control vs A/V).
    /// </summary>
    public void StartFed()
    {
        _cts = new CancellationTokenSource();
        _retransmitLoop = Task.Run(() => RetransmitLoopAsync(_cts.Token));
    }

    /// <summary>Process one control packet (a SACK or a DATA chunk) received by the caller's loop.</summary>
    public Task HandlePacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (TakionSackChunk.TryParse(packet.Span, out var sack))
        {
            HandleSack(sack.CumulativeTsnAck);
            return Task.CompletedTask;
        }

        if (TakionDataChunk.TryParse(packet, out var data))
        {
            return HandleDataAsync(data, cancellationToken);
        }

        return Task.CompletedTask;
    }

    /// <summary>Number of DATA chunks awaiting acknowledgement (diagnostics/tests).</summary>
    public int UnackedCount { get { lock (_gate) return _unacked.Count; } }

    /// <summary>
    /// Smoothed round-trip time in milliseconds from control-DATA → SACK timings, or 0 before the first clean
    /// sample. This is the real network RTT — the number a player experiences as input lag.
    ///
    /// <para>
    /// Deliberately a <see cref="double"/>. Reported as an integer it read "0 ms" on a wired LAN, where the true
    /// value is a few hundred microseconds — technically correct, entirely useless, and indistinguishable from
    /// "not implemented". Sub-millisecond resolution also makes the difference between a 0.4 ms wired link and a
    /// 3 ms Wi-Fi link visible, which is the comparison that actually matters for a handheld.
    /// </para>
    /// </summary>
    public double RoundTripTimeMs { get { lock (_gate) return _smoothedRttMs; } }

    /// <summary>
    /// How many clean RTT samples have been folded into the estimate. Exposed because "0 ms" is ambiguous
    /// between a very fast link and a metric that is not being fed: a non-zero count settles that question
    /// without a debugger.
    /// </summary>
    public long RoundTripSampleCount { get { lock (_gate) return _rttSampleCount; } }

    /// <summary>EWMA weight for a new RTT sample (RFC 6298 uses 1/8 for SRTT).</summary>
    private const double RttSmoothingAlpha = 0.125;

    /// <summary>
    /// Install a per-packet sealer applied to outgoing control DATA (in place). Set after the stream key
    /// agreement so subsequent control messages (STREAM_INFO_ACK, heartbeats) carry the required GMAC.
    /// Messages sent before this (INIT/COOKIE/SESSION_REQUEST) go out unauthenticated, as the console expects.
    /// </summary>
    public void EnableSealing(Action<byte[]> sealer)
    {
        lock (_gate)
        {
            _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
        }
    }

    /// <summary>Send a control message reliably on <paramref name="channel"/>, fragmenting as needed.</summary>
    public async Task SendMessageAsync(ushort channel, ControlMessage message, CancellationToken cancellationToken)
    {
        byte[] body = message.ToByteArray();
        int offset = 0;
        do
        {
            int take = Math.Min(MaxPayloadPerChunk, body.Length - offset);
            bool end = offset + take >= body.Length;
            bool first = offset == 0;
            uint tsn;
            byte[] packet;
            lock (_gate)
            {
                tsn = _nextSendTsn++;
                packet = TakionDataChunk.Build(_remoteTag, tsn, channel, body.AsSpan(offset, take), endOfMessage: end, firstFragment: first);
                // Once the stream keys are up, the console requires control DATA to be GMAC-authenticated
                // (key position + tag written into the header); unauthenticated packets are dropped. Seal
                // once here so retransmits resend the identical sealed bytes.
                _sealer?.Invoke(packet);
                _unacked[tsn] = packet;
                _sendTimestamps[tsn] = Stopwatch.GetTimestamp();
            }

            await _channel.SendAsync(packet, _remote, cancellationToken).ConfigureAwait(false);
            offset += take;
        }
        while (offset < body.Length);
    }

    /// <summary>Await the next fully-reassembled inbound control message.</summary>
    public ValueTask<ControlMessage> ReceiveMessageAsync(CancellationToken cancellationToken)
        => _inbound.Reader.ReadAsync(cancellationToken);

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!result.RemoteEndPoint.Equals(_remote))
                {
                    continue;
                }

                await HandlePacketAsync(result.Buffer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void HandleSack(uint cumulativeTsnAck)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();

            // Remove everything acknowledged up to and including the cumulative TSN.
            while (_unacked.Count > 0)
            {
                uint lowest = _unacked.Keys.First();
                if (TsnLessOrEqual(lowest, cumulativeTsnAck))
                {
                    _unacked.Remove(lowest);

                    // Fold this chunk's round trip into the estimate, unless it was retransmitted (Karn).
                    if (_sendTimestamps.Remove(lowest, out long sentAt) && !_retransmitted.Remove(lowest))
                    {
                        double sampleMs = (now - sentAt) * 1000.0 / Stopwatch.Frequency;

                        // Seed from the first sample, then smooth. Note the seed test is on the sample count,
                        // not on the value: a genuine sub-millisecond first sample would otherwise look like
                        // "no estimate yet" forever and the EWMA would keep re-seeding.
                        _smoothedRttMs = _rttSampleCount == 0
                            ? sampleMs
                            : ((1 - RttSmoothingAlpha) * _smoothedRttMs) + (RttSmoothingAlpha * sampleMs);
                        _rttSampleCount++;
                    }
                }
                else
                {
                    break;
                }
            }
        }
    }

    private async Task HandleDataAsync(TakionDataChunk.Parsed data, CancellationToken cancellationToken)
    {
        bool inOrder;
        lock (_gate)
        {
            if (!_haveExpected)
            {
                _expectedRecvTsn = data.Seq; // the peer's initial TSN
                _haveExpected = true;
            }
            inOrder = data.Seq == _expectedRecvTsn;
            if (inOrder)
            {
                _expectedRecvTsn++;
            }
        }

        if (inOrder)
        {
            byte[]? complete = _reassembler.Accept(data);
            // Cumulative-ack the highest in-order TSN received.
            await SendSackAsync(data.Seq, cancellationToken).ConfigureAwait(false);

            if (complete is not null && TryParseControlMessage(complete, out ControlMessage? message))
            {
                _inbound.Writer.TryWrite(message!);
            }
        }
        else
        {
            // Out of order: re-ack the last in-order TSN so the peer retransmits the gap.
            uint lastInOrder;
            lock (_gate) lastInOrder = _expectedRecvTsn - 1;
            await SendSackAsync(lastInOrder, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Send a SACK, sealed once the stream keys are up. Like DATA, the console ignores an unauthenticated
    /// SACK after key agreement — so without sealing here it never sees our ack of its STREAM_INFO and
    /// eventually disconnects ("streaminfoack fail").
    /// </summary>
    private Task SendSackAsync(uint cumulativeTsn, CancellationToken cancellationToken)
    {
        byte[] sack = TakionSackChunk.Build(_remoteTag, cumulativeTsn);
        _sealer?.Invoke(sack);
        return _channel.SendAsync(sack, _remote, cancellationToken).AsTask();
    }

    private async Task RetransmitLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_retransmitInterval, cancellationToken).ConfigureAwait(false);

                byte[][] toResend;
                lock (_gate)
                {
                    toResend = _unacked.Values.ToArray();

                    // Mark these ineligible as RTT samples: once a chunk is sent twice, a later SACK can't be
                    // attributed to a specific transmission, and treating it as one would bias RTT upward by
                    // roughly a retransmit interval — exactly when an accurate RTT matters most.
                    foreach (uint tsn in _unacked.Keys)
                    {
                        _retransmitted.Add(tsn);
                    }
                }

                foreach (byte[] packet in toResend)
                {
                    await _channel.SendAsync(packet, _remote, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private static bool TryParseControlMessage(byte[] body, out ControlMessage? message)
    {
        try
        {
            message = ControlMessage.Parser.ParseFrom(body);
            return true;
        }
        catch (InvalidProtocolBufferException)
        {
            message = null;
            return false;
        }
    }

    // Serial-number comparison (RFC 1982) so a wrapped TSN space compares correctly.
    private static bool TsnLessOrEqual(uint a, uint b) => a == b || (int)(a - b) < 0;

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        foreach (Task? loop in new[] { _receiveLoop, _retransmitLoop })
        {
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); } catch { /* ignore shutdown races */ }
            }
        }

        _cts?.Dispose();
        _inbound.Writer.TryComplete();
    }
}
