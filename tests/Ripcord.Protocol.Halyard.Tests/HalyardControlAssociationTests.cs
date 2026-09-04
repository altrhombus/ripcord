using System.Buffers.Binary;
using System.Text;
using Ripcord.Protocol.Halyard.Common.Control;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The control association's state machine.
///
/// <para>
/// Every test here is synchronous and deterministic, which is the whole reason the machine has no I/O: the
/// protocol's awkward part is <em>who does what when</em>, and that had been discovered by running against a
/// console and reading a packet capture. Here it is asserted.
/// </para>
///
/// <para>
/// The random source is injected and counts up, so a whole exchange is reproducible and the bytes the
/// association emits can be predicted rather than merely shaped.
/// </para>
/// </summary>
public class HalyardControlAssociationTests
{
    private static readonly byte[] OurId = [.. Enumerable.Range(1, 20).Select(i => (byte)i)];
    private static readonly byte[] PeerId = [.. Enumerable.Range(101, 20).Select(i => (byte)i)];

    /// <summary>Deterministic "random": a counter, so every generated value is predictable and distinct.</summary>
    private static Func<int, byte[]> Counter()
    {
        byte next = 0x40;
        return count => [.. Enumerable.Range(0, count).Select(_ => next++)];
    }

    private static HalyardControlAssociation New() => new(OurId, PeerId, Counter());

    private static HalyardControlPrelude ParsePrelude(byte[] datagram)
    {
        Assert.True(HalyardControlPrelude.TryParse(datagram, out HalyardControlPrelude prelude));
        return prelude;
    }

    private static HalyardControlChunk ParseChunk(byte[] datagram)
        => Assert.Single(HalyardControlChunkCodec.ReadAll(datagram));

    private static byte[] PeerInit(uint tagPair, uint token)
        => new HalyardControlPrelude(
            HalyardControlPrelude.Init, PeerId, OurId, tagPair, RequestWord: 0x19, Token: token,
            Tail: ReadOnlyMemory<byte>.Empty).Serialize();

    private static byte[] PeerEcho(uint tagPair, uint token)
        => new HalyardControlPrelude(
            HalyardControlPrelude.CookieEcho, PeerId, OurId, tagPair, RequestWord: 0, Token: token,
            Tail: ReadOnlyMemory<byte>.Empty).Serialize();

    // ---- the prelude, as initiator --------------------------------------------------------------

    [Fact]
    public void Open_AnnouncesUsWithTheInitiatorsRequestWord()
    {
        HalyardControlAssociation association = New();

        HalyardControlPrelude init = ParsePrelude(Assert.Single(association.Open().Send));

        Assert.Equal(HalyardControlPrelude.Init, init.Type);
        Assert.Equal(OurId, init.SenderId.ToArray());
        Assert.Equal(PeerId, init.PeerId.ToArray());
        // 0x40 is what a same-LAN vendor client sends; a WAN one sends 0x19 and what selects it is [X].
        Assert.Equal(0x40u, init.RequestWord);
        Assert.NotEqual(0u, init.Token);
        Assert.Equal(HalyardControlPhase.Handshaking, association.Phase);
    }

    [Fact]
    public void AsInitiator_TheAnswerToOurInitIsEchoed_AndTheHandshakeSettlesOnTheirEcho()
    {
        HalyardControlAssociation association = New();
        HalyardControlPrelude ours = ParsePrelude(Assert.Single(association.Open().Send));

        // The peer answers ours: same tag pair, halves exchanged.
        HalyardControlAction answered = association.OnDatagram(
            PeerInit(SwapHalves(ours.TagPair), token: 0xAABBCCDD));

        HalyardControlPrelude echo = ParsePrelude(Assert.Single(answered.Send));
        Assert.Equal(HalyardControlPrelude.CookieEcho, echo.Type);
        Assert.Equal(0xAABBCCDDu, echo.Token);          // their token, returned
        Assert.Empty(answered.Events);                   // not established until they echo too

        HalyardControlAction settled = association.OnDatagram(PeerEcho(ours.TagPair, ours.Token));

        Assert.IsType<HalyardControlEvent.PreludeEstablished>(Assert.Single(settled.Events));
        Assert.Equal(HalyardControlPhase.Established, association.Phase);
    }

    // ---- the prelude, as responder --------------------------------------------------------------

    [Fact]
    public void AsResponder_WeAnswerWithTheTagPairExchangedAndAZeroRequestWord()
    {
        // The two roles differ in exactly these two fields, which is why a wrongly-shaped answer is ignored
        // rather than refused — a live console re-sent its Init fourteen times over one.
        HalyardControlAssociation association = New();

        HalyardControlAction action = association.OnDatagram(PeerInit(0x00017777, token: 0x11223344));

        Assert.Equal(2, action.Send.Count);

        HalyardControlPrelude answer = ParsePrelude(action.Send[0]);
        Assert.Equal(HalyardControlPrelude.Init, answer.Type);
        Assert.Equal(0x77770001u, answer.TagPair);
        Assert.Equal(0u, answer.RequestWord);
        Assert.Equal(OurId, answer.SenderId.ToArray());
    }

    [Fact]
    public void AsResponder_WeAlsoEchoTheirToken_BecauseBothSidesEcho()
    {
        // Answering with only an Init leaves the peer re-sending its own indefinitely.
        HalyardControlAssociation association = New();

        HalyardControlAction action = association.OnDatagram(PeerInit(0x00017777, token: 0x11223344));

        HalyardControlPrelude echo = ParsePrelude(action.Send[1]);
        Assert.Equal(HalyardControlPrelude.CookieEcho, echo.Type);
        Assert.Equal(0x11223344u, echo.Token);
        Assert.Equal(0x77770001u, echo.TagPair);
    }

    [Fact]
    public void AsResponder_TheHandshakeSettlesWhenTheyEcho()
    {
        HalyardControlAssociation association = New();
        association.OnDatagram(PeerInit(0x00017777, token: 0x11223344));

        HalyardControlAction settled = association.OnDatagram(PeerEcho(0x00017777, token: 0));

        Assert.IsType<HalyardControlEvent.PreludeEstablished>(Assert.Single(settled.Events));
        Assert.Equal(HalyardControlPhase.Established, association.Phase);
    }

    [Fact]
    public void EitherRole_ReachesTheSamePlace()
    {
        // The property the whole design exists for: which side opened is not a mode this type is built in.
        HalyardControlAssociation initiator = New();
        HalyardControlPrelude ours = ParsePrelude(Assert.Single(initiator.Open().Send));
        initiator.OnDatagram(PeerInit(SwapHalves(ours.TagPair), 0xAABBCCDD));
        initiator.OnDatagram(PeerEcho(ours.TagPair, ours.Token));

        HalyardControlAssociation responder = New();
        responder.OnDatagram(PeerInit(0x00017777, 0x11223344));
        responder.OnDatagram(PeerEcho(0x00017777, 0));

        Assert.Equal(HalyardControlPhase.Established, initiator.Phase);
        Assert.Equal(HalyardControlPhase.Established, responder.Phase);
    }

    [Fact]
    public void EveryPeerInit_IsEchoed_NotJustTheFirst()
    {
        // This test previously asserted the opposite, on the reasonable-sounding grounds that a repeated echo
        // would be noise. The wire disagrees: the peer probes for as long as the association lives — every half
        // second, each probe carrying a fresh timestamp — and the captured client answers every one. Echoing
        // once and going quiet is how a live console came to give up on us after ten seconds.
        HalyardControlAssociation association = New();
        association.OnDatagram(PeerInit(0x00017777, 0x11223344));

        HalyardControlAction again = association.OnDatagram(PeerInit(0x00017777, token: 0x11229999));

        HalyardControlPrelude echo = Assert.Single(
            again.Send.Select(ParsePrelude).Where(p => p.Type == HalyardControlPrelude.CookieEcho));
        Assert.Equal(0x11229999u, echo.Token);   // the LATEST probe's timestamp, not the first
    }

    [Fact]
    public void AnEchoedProbe_DoesNotReopenTheAssociation()
    {
        // Answering every probe must not mean re-answering with an Init each time, which would look like a
        // fresh association attempt.
        HalyardControlAssociation association = New();
        association.OnDatagram(PeerInit(0x00017777, 0x11223344));

        HalyardControlAction again = association.OnDatagram(PeerInit(0x00017777, 0x11229999));

        Assert.DoesNotContain(again.Send, d => ParsePrelude(d).Type == HalyardControlPrelude.Init);
    }

    // ---- the chunk layer -------------------------------------------------------------------------

    private static HalyardControlAssociation Established()
    {
        HalyardControlAssociation association = New();
        association.OnDatagram(PeerInit(0x00017777, 0x11223344));
        association.OnDatagram(PeerEcho(0x00017777, 0));
        return association;
    }

    [Fact]
    public void OpenConnection_SendsAHelloCarryingTheCapabilityBlockAndWindow()
    {
        HalyardControlAssociation association = Established();

        HalyardControlChunk hello = ParseChunk(Assert.Single(association.OpenConnection().Send));

        Assert.Equal(HalyardControlChunkType.Hello, hello.Type);
        Assert.Equal(new byte[] { 0x0B, 0x01, 0x01, 0x00, 0x01, 0x00 }, hello.Body[2..8].ToArray());
        Assert.Equal(0x0582, BinaryPrimitives.ReadUInt16BigEndian(hello.Body.Span[^2..]));
    }

    [Fact]
    public void OpenConnection_BeforeThePreludeIsEstablished_DoesNothing()
    {
        HalyardControlAssociation association = New();

        Assert.Empty(association.OpenConnection().Send);
    }

    [Fact]
    public void AsConnectionInitiator_TheCookieComesBackAppendedToTheHello()
    {
        HalyardControlAssociation association = Established();
        HalyardControlChunk hello = ParseChunk(Assert.Single(association.OpenConnection().Send));

        byte[] cookieBody = [.. Enumerable.Range(0, 42).Select(i => (byte)(0xE0 + i))];
        HalyardControlAction action = association.OnDatagram(
            HalyardControlChunkCodec.Encode(HalyardControlChunkType.Cookie, 0x00, cookieBody));

        HalyardControlChunk echo = ParseChunk(Assert.Single(action.Send));
        Assert.Equal(HalyardControlChunkType.HelloEcho, echo.Type);
        Assert.Equal(hello.Body.ToArray(), echo.Body[..hello.Body.Length].ToArray());
        Assert.Equal(cookieBody, echo.Body[hello.Body.Length..].ToArray());
    }

    [Fact]
    public void AsConnectionInitiator_AnAcceptOpensTheConnection()
    {
        HalyardControlAssociation association = Established();
        association.OpenConnection();

        byte[] accept = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(accept, 0xA65E);          // the peer's sequence
        BinaryPrimitives.WriteUInt16BigEndian(accept.AsSpan(2), 0x2BFA); // acking ours
        HalyardControlAction action = association.OnDatagram(
            HalyardControlChunkCodec.Encode(HalyardControlChunkType.Accept, 0x30, accept));

        var opened = Assert.IsType<HalyardControlEvent.ConnectionOpened>(Assert.Single(action.Events));
        Assert.False(opened.OpenedByPeer);
        Assert.Equal(HalyardControlPhase.Connected, association.Phase);
    }

    [Fact]
    public void AsConnectionResponder_AHelloIsAnsweredWithACookie_AndAnEchoOpensTheConnection()
    {
        // No capture shows this side of the chunk handshake — every recording we hold has the client opening
        // one — so this pins the shape we send rather than claiming it is confirmed. See the [X] in the
        // association.
        HalyardControlAssociation association = Established();

        byte[] peerHello = new byte[14];
        BinaryPrimitives.WriteUInt16BigEndian(peerHello, 0x1234);
        HalyardControlAction cookie = association.OnDatagram(
            HalyardControlChunkCodec.Encode(HalyardControlChunkType.Hello, 0x30, peerHello));

        Assert.Equal(HalyardControlChunkType.Cookie, ParseChunk(Assert.Single(cookie.Send)).Type);

        HalyardControlAction opened = association.OnDatagram(
            HalyardControlChunkCodec.Encode(HalyardControlChunkType.HelloEcho, 0x30, peerHello));

        Assert.Equal(HalyardControlChunkType.Accept, ParseChunk(Assert.Single(opened.Send)).Type);
        var connection = Assert.IsType<HalyardControlEvent.ConnectionOpened>(Assert.Single(opened.Events));
        Assert.True(connection.OpenedByPeer);
        Assert.Equal(HalyardControlPhase.Connected, association.Phase);
    }

    [Fact]
    public void Send_PutsAnAcknowledgementInFrontOfTheDataInOneDatagram()
    {
        HalyardControlAssociation association = Connected();

        byte[] datagram = Assert.Single(association.Send("POST /x HTTP/1.1\r\n\r\n"u8.ToArray()).Send);

        List<HalyardControlChunk> chunks = HalyardControlChunkCodec.ReadAll(datagram);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(HalyardControlChunkType.Ack, chunks[0].Type);
        Assert.Equal(HalyardControlChunkType.Data, chunks[1].Type);
        Assert.Equal("POST /x HTTP/1.1\r\n\r\n", Encoding.ASCII.GetString(chunks[1].Body[2..].Span));
    }

    [Fact]
    public void Send_BeforeAConnectionIsOpen_DoesNothing()
    {
        Assert.Empty(Established().Send("POST /x"u8.ToArray()).Send);
    }

    [Fact]
    public void Data_AccumulatesAcrossChunks()
    {
        // A datagram boundary is not a message boundary.
        HalyardControlAssociation association = Connected();

        association.OnDatagram(DataChunk(1, "HTTP/1.1 200 OK\r\n"));
        HalyardControlAction second = association.OnDatagram(DataChunk(2, "Content-Length: 0\r\n\r\n"));

        var data = Assert.IsType<HalyardControlEvent.DataReceived>(Assert.Single(second.Events));
        Assert.Equal("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", Encoding.ASCII.GetString(data.Payload));
    }

    [Fact]
    public void Close_EndsTheAssociation()
    {
        HalyardControlAssociation association = Connected();

        HalyardControlAction action = association.OnDatagram(
            HalyardControlChunkCodec.Encode(HalyardControlChunkType.Close, 0x00, new byte[8]));

        Assert.IsType<HalyardControlEvent.PeerClosed>(Assert.Single(action.Events));
        Assert.Equal(HalyardControlPhase.Closed, association.Phase);
    }

    [Fact]
    public void Acknowledgements_AreAcceptedSilently()
    {
        HalyardControlAssociation association = Connected();

        HalyardControlAction action = association.OnDatagram(
            HalyardControlChunkCodec.Encode(HalyardControlChunkType.Ack, 0x30, new byte[4]));

        Assert.Empty(action.Send);
        Assert.Empty(action.Events);
    }

    // ---- the diagnostic property -----------------------------------------------------------------

    [Fact]
    public void Rubbish_IsReportedRatherThanDropped()
    {
        // The reason this event exists: the first live runs of this transport could only report "the console
        // sent nothing", which said nothing about what it had sent.
        HalyardControlAssociation association = Established();

        HalyardControlAction action = association.OnDatagram([1, 2, 3, 4]);

        var unhandled = Assert.IsType<HalyardControlEvent.Unhandled>(Assert.Single(action.Events));
        Assert.Contains("neither a prelude nor", unhandled.Reason);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, unhandled.Datagram);
    }

    [Fact]
    public void ACookieForAConnectionWeNeverOpened_IsReported()
    {
        HalyardControlAssociation association = Established();

        HalyardControlAction action = association.OnDatagram(
            HalyardControlChunkCodec.Encode(HalyardControlChunkType.Cookie, 0x00, new byte[42]));

        var unhandled = Assert.IsType<HalyardControlEvent.Unhandled>(Assert.Single(action.Events));
        Assert.Contains("never opened", unhandled.Reason);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static HalyardControlAssociation Connected()
    {
        HalyardControlAssociation association = Established();
        association.OpenConnection();

        byte[] accept = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(accept, 0xA65E);
        BinaryPrimitives.WriteUInt16BigEndian(accept.AsSpan(2), 0x2BFA);
        association.OnDatagram(HalyardControlChunkCodec.Encode(HalyardControlChunkType.Accept, 0x30, accept));
        return association;
    }

    private static byte[] DataChunk(ushort sequence, string text)
    {
        byte[] body = new byte[2 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, sequence);
        Encoding.ASCII.GetBytes(text).CopyTo(body, 2);
        return HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, body);
    }

    private static uint SwapHalves(uint value) => (value >> 16) | (value << 16);
}
