using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Common.Control;

/// <summary>Something the association observed that a caller may care about.</summary>
public abstract record HalyardControlEvent
{
    /// <summary>The prelude is complete in both directions; chunk traffic may begin.</summary>
    public sealed record PreludeEstablished : HalyardControlEvent;

    /// <summary>A chunk-layer connection is open, whichever side opened it.</summary>
    /// <param name="OpenedByPeer">True when the peer opened it and we answered.</param>
    public sealed record ConnectionOpened(bool OpenedByPeer) : HalyardControlEvent;

    /// <summary>Payload arrived on the open connection — HTTP text, or a binary control frame.</summary>
    public sealed record DataReceived(byte[] Payload) : HalyardControlEvent;

    /// <summary>The peer tore the connection down.</summary>
    public sealed record PeerClosed : HalyardControlEvent;

    /// <summary>
    /// A datagram the association did not know what to do with.
    ///
    /// <para>
    /// Reported rather than dropped, and this is deliberate: the first live runs of this transport reported
    /// only "the console sent nothing", which said nothing about what it <em>had</em> sent. An association
    /// that names what it ignored turns a silent stall into evidence.
    /// </para>
    /// </summary>
    public sealed record Unhandled(string Reason, byte[] Datagram) : HalyardControlEvent;
}

/// <summary>What to do as a result of feeding the association something: bytes to put on the wire, and what
/// happened.</summary>
public sealed record HalyardControlAction(
    IReadOnlyList<byte[]> Send,
    IReadOnlyList<HalyardControlEvent> Events)
{
    public static readonly HalyardControlAction None = new([], []);
}

/// <summary>How far the association has got.</summary>
public enum HalyardControlPhase
{
    /// <summary>Nothing sent or received yet.</summary>
    Idle,

    /// <summary>Prelude in progress — we have sent something and are waiting on the peer.</summary>
    Handshaking,

    /// <summary>Prelude complete in both directions.</summary>
    Established,

    /// <summary>A chunk-layer connection is open.</summary>
    Connected,

    /// <summary>The peer closed the connection.</summary>
    Closed,
}

/// <summary>
/// How a chunk we originate addresses the peer — the chunk header's word count, named for what it selects.
///
/// <para>
/// The receiver looks a connection up by the last port word a chunk carries, and a chunk carrying none is
/// matched on the peer address record instead. Which of those we use is therefore a real protocol choice and
/// not a formatting detail: it decides <em>which</em> of the peer's connection objects our chunk can reach.
/// </para>
/// </summary>
public enum HalyardControlAddressing
{
    /// <summary>
    /// Word count 3: a (source, destination) port pair, both the control port. What every capture we hold
    /// carries, and the default.
    /// </summary>
    PortPair = 3,

    /// <summary>
    /// Word count 2: a single port word, which the receiver requires to equal <em>both</em> the local and the
    /// peer port of the connection it selects. Never observed; included because the receiver accepts it.
    /// </summary>
    SinglePort = 2,

    /// <summary>
    /// Word count 1: no port words at all, so the receiver matches on the peer address record alone.
    ///
    /// <para>
    /// <b>[X] Never observed on the wire — this is an experiment.</b> Every hello Ripcord has sent used
    /// <see cref="PortPair"/> and the console ignored all of them in silence, which is exactly what a port
    /// lookup miss does. This shape sidesteps the port lookup, so if the console holds any connection object
    /// for our address — and it must, since it dialled us — a hello addressed this way reaches it.
    /// </para>
    /// </summary>
    PeerAddressOnly = 1,
}

/// <summary>
/// The account-route control transport as a <b>peer association</b>, driven purely by what arrives.
///
/// <para>
/// <b>Why this is not a client.</b> The first shape of this code performed one request/response cycle —
/// prelude, handshake, send, read — which assumed we were always the side that initiates. The wire says
/// otherwise: on a WAN path the client opens the prelude to punch out through NAT, and on a shared LAN the
/// <em>console</em> opens it as soon as signaling has given it our candidate. Both sides echo. Either side may
/// open a chunk connection. Encoding a role at compile time meant every new observation became a
/// restructuring, and it could never have carried the rest of the account route: the whole control plane —
/// <c>rgst</c>, <c>init</c>, <c>ctrl</c>, and then the persistent binary frames — runs over this, and a
/// one-shot exchange cannot hold a persistent channel.
/// </para>
///
/// <para>
/// <b>So this is a state machine with no I/O.</b> Feed it a datagram, get back the datagrams to send and the
/// events that occurred. No sockets, no timers, no <c>async</c> — which is what lets the whole protocol be
/// driven from a capture, in either role, as a deterministic test. The socket pump that wraps it holds no
/// protocol knowledge at all.
/// </para>
///
/// <para>
/// Derived from this project's own captures; see <c>docs/protocol/ps5-session-transport.md</c>. Points still
/// marked <c>[X]</c> there are marked here too, at the line that guesses.
/// </para>
/// </summary>
public sealed class HalyardControlAssociation
{
    /// <summary>The version/capability block every hello carries, constant in every observed connection.</summary>
    private static readonly byte[] CapabilityBlock = [0x0B, 0x01, 0x01, 0x00, 0x01, 0x00];

    /// <summary>
    /// 1410, where an MTU or receive window would sit; constant everywhere it was observed. <b>[X]</b> read as
    /// an MTU, unconfirmed — sent verbatim because that is what the console saw.
    /// </summary>
    private const ushort WindowOrMtu = 0x0582;

    /// <summary>The flags byte on nearly every chunk. <c>0x2F</c> also occurs; what selects it is <b>[X]</b>.</summary>
    private const byte DefaultFlags = 0x30;

    /// <summary>
    /// The request word the opening side sends; the answering side sends zero, and an echo mirrors whatever
    /// the Init it answers carried.
    ///
    /// <para>
    /// <b>[X] Not a constant, and this value is a choice.</b> A WAN client sends <c>0x19</c> and a same-LAN one
    /// <c>0x40</c>; what selects it is unknown. <c>0x40</c> is used here because the account route's live
    /// target is a console on the same network, so it is the value a console in that position has actually
    /// been seen to accept.
    /// </para>
    /// </summary>
    private const uint InitiatorRequestWord = 0x40;

    private readonly byte[] _localHashedId;
    private readonly byte[] _peerHashedId;
    private readonly Func<int, byte[]> _random;

    /// <summary>
    /// The peer's address and port as we are sending to them, which a <c>CookieEcho</c> has to reflect back —
    /// see <see cref="HalyardControlPrelude.ReflectPeerEndpoint"/>. Held as plain data rather than reached
    /// through a socket, so this type stays I/O-free and drivable from a capture.
    /// </summary>
    private readonly byte[] _peerAddress;
    private readonly ushort _peerPort;

    private uint _tagPair;

    /// <summary>
    /// The value we put in the token field. <b>Almost certainly a microsecond timestamp</b>: a live console's
    /// rose by ~500,190 between Inits sent half a second apart, across every probe. Ours is a fixed random
    /// value, which is structurally fine — the peer only echoes it back — but if a console measures round
    /// trips from it, this is the field to make real. <b>[X]</b>
    /// </summary>
    private uint _ourToken;
    private bool _weSentEcho;
    private bool _peerEchoed;

    private byte[]? _helloBody;

    /// <summary>
    /// How the connection we opened addresses the peer. Held so a retransmission and the cookie echo repeat
    /// the shape of the hello they belong to — a connection addressed two different ways is two attempts, not
    /// one.
    /// </summary>
    private HalyardControlAddressing _helloAddressing = HalyardControlAddressing.PortPair;
    private ushort _sequence;
    private ushort _peerSequence;
    private readonly List<byte> _inbound = [];

    /// <param name="peerAddress">
    /// The peer's IPv4 address, 4 bytes in network order — the address we send datagrams to. Required, because
    /// an echo that does not reflect it is one the peer cannot validate the path from.
    /// </param>
    /// <param name="peerPort">The peer's UDP port.</param>
    /// <param name="random">
    /// Source of the tag, token, sequence and connection tag. Injected so a test can make a whole exchange
    /// reproducible; production passes a cryptographic source.
    /// </param>
    public HalyardControlAssociation(
        ReadOnlyMemory<byte> localHashedId,
        ReadOnlyMemory<byte> peerHashedId,
        ReadOnlyMemory<byte> peerAddress,
        ushort peerPort,
        Func<int, byte[]> random)
    {
        _localHashedId = Exactly(localHashedId, nameof(localHashedId));
        _peerHashedId = Exactly(peerHashedId, nameof(peerHashedId));

        if (peerAddress.Length != 4)
        {
            throw new ArgumentException(
                $"peerAddress is an IPv4 address, so 4 bytes; got {peerAddress.Length}.", nameof(peerAddress));
        }

        _peerAddress = peerAddress.ToArray();
        _peerPort = peerPort;
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    private static byte[] Exactly(ReadOnlyMemory<byte> id, string name)
        => id.Length == HalyardControlPrelude.HashedIdLength
            ? id.ToArray()
            : throw new ArgumentException(
                $"A localHashedId is {HalyardControlPrelude.HashedIdLength} bytes; got {id.Length}.", name);

    public HalyardControlPhase Phase { get; private set; } = HalyardControlPhase.Idle;

    /// <summary>Open the prelude ourselves. Safe to call when the peer has already opened it — the association
    /// simply answers what arrives — but on a NAT path somebody has to send first.</summary>
    public HalyardControlAction Open()
    {
        if (Phase != HalyardControlPhase.Idle)
        {
            return HalyardControlAction.None;
        }

        _tagPair = 0x00010000u | BinaryPrimitives.ReadUInt16BigEndian(_random(2));
        _ourToken = BinaryPrimitives.ReadUInt32BigEndian(_random(4));
        Phase = HalyardControlPhase.Handshaking;

        return Datagrams(Prelude(HalyardControlPrelude.Init, InitiatorRequestWord, _ourToken));
    }

    /// <summary>Re-send whatever the current phase is waiting on. The prelude is lossy by nature — on a WAN
    /// path early probes are expected to be dropped — so a pump calls this on a timer.</summary>
    public HalyardControlAction Retry()
        => Phase switch
        {
            HalyardControlPhase.Handshaking when _weSentEcho => HalyardControlAction.None,
            HalyardControlPhase.Handshaking => Datagrams(
                Prelude(HalyardControlPrelude.Init, InitiatorRequestWord, _ourToken)),
            _ => HalyardControlAction.None,
        };

    /// <summary>Open a chunk-layer connection. Only meaningful once the prelude is established.</summary>
    /// <param name="addressing">
    /// How the hello addresses the peer. Defaults to the shape every capture carries; the others exist so the
    /// question of which of the peer's connection objects a chunk can reach is testable against hardware.
    /// </param>
    public HalyardControlAction OpenConnection(
        HalyardControlAddressing addressing = HalyardControlAddressing.PortPair)
    {
        if (Phase != HalyardControlPhase.Established)
        {
            return HalyardControlAction.None;
        }

        _helloAddressing = addressing;
        _sequence = BinaryPrimitives.ReadUInt16BigEndian(_random(2));
        _helloBody = HelloBody(_sequence, _random(4));
        return Datagrams(HalyardControlChunkCodec.Encode(
            HalyardControlChunkType.Hello, DefaultFlags, _helloBody, (byte)_helloAddressing));
    }

    /// <summary>
    /// Re-send the hello for a connection we have already tried to open, unchanged.
    ///
    /// <para>
    /// The same bytes deliberately: this is a retransmission, not a second connection, and the captured client
    /// re-sends an identical hello when its first goes unanswered.
    /// </para>
    /// </summary>
    public HalyardControlAction ReopenConnection()
        => Phase == HalyardControlPhase.Established && _helloBody is not null
            ? Datagrams(HalyardControlChunkCodec.Encode(
                HalyardControlChunkType.Hello, DefaultFlags, _helloBody, (byte)_helloAddressing))
            : HalyardControlAction.None;

    /// <summary>
    /// Send a payload on the open connection — an HTTP request, or a binary control frame.
    ///
    /// <para>
    /// Prefixed by a bare acknowledgement in the same datagram, which is the shape the captured client sends
    /// and the reason the chunk codec has to handle concatenation.
    /// </para>
    /// </summary>
    public HalyardControlAction Send(ReadOnlyMemory<byte> payload)
    {
        if (Phase != HalyardControlPhase.Connected)
        {
            return HalyardControlAction.None;
        }

        byte[] ack = HalyardControlChunkCodec.Encode(
            HalyardControlChunkType.Ack, DefaultFlags, SequencePair(_sequence, (ushort)(_peerSequence + 1)));
        byte[] data = HalyardControlChunkCodec.Encode(
            HalyardControlChunkType.Data, DefaultFlags, WithSequence(_sequence, payload.Span));

        return Datagrams(Concat(ack, data));
    }

    /// <summary>Feed one datagram. Everything this association does is a reaction to this.</summary>
    public HalyardControlAction OnDatagram(ReadOnlySpan<byte> datagram)
    {
        if (HalyardControlPrelude.TryParse(datagram, out HalyardControlPrelude prelude))
        {
            return OnPrelude(prelude);
        }

        List<HalyardControlChunk> chunks = HalyardControlChunkCodec.ReadAll(datagram);
        if (chunks.Count == 0)
        {
            return Event(new HalyardControlEvent.Unhandled(
                "neither a prelude nor a well-formed chunk", datagram.ToArray()));
        }

        var send = new List<byte[]>();
        var events = new List<HalyardControlEvent>();
        foreach (HalyardControlChunk chunk in chunks)
        {
            HalyardControlAction action = OnChunk(chunk, datagram);
            send.AddRange(action.Send);
            events.AddRange(action.Events);
        }

        return new HalyardControlAction(send, events);
    }

    // ---- the prelude ---------------------------------------------------------------------------

    private HalyardControlAction OnPrelude(HalyardControlPrelude peer)
    {
        if (peer.Type == HalyardControlPrelude.CookieEcho)
        {
            _peerEchoed = true;
            return SettleIfEstablished();
        }

        // An Init. Either it answers ours, or the peer is opening the association itself — the two differ only
        // in whose tag pair it carries, which is why a wrongly-shaped answer is ignored rather than refused.
        var send = new List<byte[]>();

        if (Phase == HalyardControlPhase.Idle || peer.TagPair != SwapHalves(_tagPair))
        {
            // The peer opened it. Adopt its tag pair, exchanged, and answer in the responder's shape: a zero
            // request word, our own token.
            _tagPair = SwapHalves(peer.TagPair);
            if (_ourToken == 0)
            {
                _ourToken = BinaryPrimitives.ReadUInt32BigEndian(_random(4));
            }

            Phase = HalyardControlPhase.Handshaking;
            send.Add(Prelude(HalyardControlPrelude.Init, requestWord: 0, token: _ourToken));
        }

        // Echo the peer's token back — for EVERY Init, not just the first. The peer keeps probing for as long
        // as the association lives, and each probe carries a fresh timestamp; a client that echoes once and
        // then goes quiet has stopped answering. A live console probed every half second and gave up after ten
        // seconds of our silence, and the captured client answers every one.
        send.Add(Prelude(
            HalyardControlPrelude.CookieEcho,
            requestWord: 0,
            token: peer.Token,
            tail: HalyardControlPrelude.ReflectPeerEndpoint(_peerAddress, _peerPort, _tagPair)));
        _weSentEcho = true;

        HalyardControlAction settled = SettleIfEstablished();
        return new HalyardControlAction([.. send, .. settled.Send], settled.Events);
    }

    private HalyardControlAction SettleIfEstablished()
    {
        if (Phase == HalyardControlPhase.Established || Phase == HalyardControlPhase.Connected)
        {
            return HalyardControlAction.None;
        }

        if (!_weSentEcho || !_peerEchoed)
        {
            return HalyardControlAction.None;
        }

        Phase = HalyardControlPhase.Established;
        return Event(new HalyardControlEvent.PreludeEstablished());
    }

    // ---- the chunk layer -----------------------------------------------------------------------

    private HalyardControlAction OnChunk(HalyardControlChunk chunk, ReadOnlySpan<byte> datagram)
    {
        switch (chunk.Type)
        {
            case HalyardControlChunkType.Cookie:
                // We opened the connection and the peer wants its cookie back. Appended verbatim rather than
                // interpreted: its internal layout is not needed to complete the handshake, and inventing a
                // reading for bytes we only hand back would be a guess on the wire.
                if (_helloBody is null)
                {
                    return Event(new HalyardControlEvent.Unhandled(
                        "a cookie arrived for a connection we never opened", datagram.ToArray()));
                }

                return Datagrams(HalyardControlChunkCodec.Encode(
                    HalyardControlChunkType.HelloEcho,
                    DefaultFlags,
                    Concat(_helloBody, chunk.Body.Span),
                    (byte)_helloAddressing));

            case HalyardControlChunkType.Accept:
                if (chunk.Body.Length < 4)
                {
                    return Event(new HalyardControlEvent.Unhandled(
                        "an accept too short to carry a sequence and an acknowledgement", datagram.ToArray()));
                }

                _peerSequence = BinaryPrimitives.ReadUInt16BigEndian(chunk.Body.Span);
                _sequence = BinaryPrimitives.ReadUInt16BigEndian(chunk.Body.Span[2..]);
                Phase = HalyardControlPhase.Connected;
                return Event(new HalyardControlEvent.ConnectionOpened(OpenedByPeer: false));

            case HalyardControlChunkType.Hello:
                // The peer is opening a connection. **[X]** No capture shows this side of it — every recording
                // we hold has the client opening — so the cookie we send back mirrors the console's own shape
                // with fresh bytes where its cookie sat. If a console ignores it, this is the line to suspect.
                _peerSequence = chunk.Body.Length >= 2
                    ? BinaryPrimitives.ReadUInt16BigEndian(chunk.Body.Span)
                    : (ushort)0;
                return Datagrams(HalyardControlChunkCodec.Encode(
                    HalyardControlChunkType.Cookie, 0x00, PeerCookieBody(), chunk.WordCount));

            case HalyardControlChunkType.HelloEcho:
                // It returned our cookie; the connection is open with us as the responder.
                _sequence = BinaryPrimitives.ReadUInt16BigEndian(_random(2));
                Phase = HalyardControlPhase.Connected;
                return new HalyardControlAction(
                    [HalyardControlChunkCodec.Encode(
                        HalyardControlChunkType.Accept,
                        DefaultFlags,
                        Concat(SequencePair(_sequence, (ushort)(_peerSequence + 1)), CapabilityTail()),
                        chunk.WordCount)],
                    [new HalyardControlEvent.ConnectionOpened(OpenedByPeer: true)]);

            case HalyardControlChunkType.Data:
                if (chunk.Body.Length > 2)
                {
                    _peerSequence = BinaryPrimitives.ReadUInt16BigEndian(chunk.Body.Span);
                    _inbound.AddRange(chunk.Body[2..].ToArray());
                    return Event(new HalyardControlEvent.DataReceived([.. _inbound]));
                }

                return HalyardControlAction.None;

            case HalyardControlChunkType.Ack:
            case HalyardControlChunkType.AckExtended:
                // Nothing is retransmitted at this layer yet, so an acknowledgement is only information.
                return HalyardControlAction.None;

            case HalyardControlChunkType.Close:
                Phase = HalyardControlPhase.Closed;
                return Event(new HalyardControlEvent.PeerClosed());

            default:
                return Event(new HalyardControlEvent.Unhandled(
                    $"chunk type {(byte)chunk.Type:X2}", datagram.ToArray()));
        }
    }

    /// <summary>Everything after this connection's payload has been delivered, reset for the next message.</summary>
    public void ClearInbound() => _inbound.Clear();

    // ---- wire helpers --------------------------------------------------------------------------

    /// <param name="tail">
    /// Empty for an <c>Init</c>, which carries a zero tail in every capture; a <c>CookieEcho</c> passes the
    /// reflected peer endpoint.
    /// </param>
    private byte[] Prelude(uint type, uint requestWord, uint token, ReadOnlyMemory<byte> tail = default)
        => new HalyardControlPrelude(
            type, _localHashedId, _peerHashedId, _tagPair, requestWord, token, tail).Serialize();

    private byte[] HelloBody(ushort sequence, ReadOnlySpan<byte> connectionTag)
    {
        byte[] body = new byte[2 + CapabilityBlock.Length + 4 + 2];
        BinaryPrimitives.WriteUInt16BigEndian(body, sequence);
        CapabilityBlock.CopyTo(body.AsSpan(2));
        connectionTag.CopyTo(body.AsSpan(2 + CapabilityBlock.Length));
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2 + CapabilityBlock.Length + 4), WindowOrMtu);
        return body;
    }

    /// <summary>The capability block and window an accept carries after its sequence pair.</summary>
    private byte[] CapabilityTail() => [.. CapabilityBlock, .. _random(4), .. WindowBytes()];

    private static byte[] WindowBytes()
    {
        byte[] window = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(window, WindowOrMtu);
        return window;
    }

    /// <summary>
    /// The cookie we offer when the peer opens a connection, mirroring the console's own 42-byte shape: the
    /// fixed words it sends, then 24 bytes of ours to be handed back. <b>[X]</b> — see the Hello case.
    /// </summary>
    private byte[] PeerCookieBody()
        => [0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x81, 0x20, 0x00, 0x96,
            0x84, 0xD0, 0x00, 0x00, 0x00, 0x00, .. _random(24)];

    private static uint SwapHalves(uint value) => (value >> 16) | (value << 16);

    private static byte[] SequencePair(ushort sequence, ushort acknowledgement)
    {
        byte[] pair = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(pair, sequence);
        BinaryPrimitives.WriteUInt16BigEndian(pair.AsSpan(2), acknowledgement);
        return pair;
    }

    private static byte[] WithSequence(ushort sequence, ReadOnlySpan<byte> payload)
    {
        byte[] body = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, sequence);
        payload.CopyTo(body.AsSpan(2));
        return body;
    }

    private static byte[] Concat(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        byte[] joined = new byte[first.Length + second.Length];
        first.CopyTo(joined);
        second.CopyTo(joined.AsSpan(first.Length));
        return joined;
    }

    private static HalyardControlAction Datagrams(params byte[][] datagrams) => new(datagrams, []);

    private static HalyardControlAction Event(HalyardControlEvent raised) => new([], [raised]);
}
