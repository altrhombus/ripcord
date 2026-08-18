using Ripcord.Cloud.Halyard;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Parsing the console's signaling out of the push channel — the inbound half of the WAN rendezvous.
///
/// <para>
/// The structure these assert against was recovered by decoding the push WebSocket (cap66/cap67, PS5 and PS4
/// connects captured through a forced transparent proxy). The frames here are synthetic and carry only
/// placeholder addresses — the real captured frames drive <see cref="LiveSignalingVectorTests"/> from the
/// dirty room instead — but the envelope shape (push notification → <c>sessionMessage.payload</c> →
/// <c>ver=1.0, type=text, body=&lt;JSON&gt;</c> → <c>connRequest</c>) is exactly what was observed.
/// </para>
/// </summary>
public class HalyardSignalingMessageTests
{
    private static string Frame(string fromPlatform, string innerBody, string dataType = "sessionMessage:created")
        => "{\"version\":\"2.1\",\"method\":3001,"
           + "\"dataType\":\"psn:sessionManager:sys:rps:" + dataType + "\","
           + "\"to\":{\"accountId\":1,\"onlineId\":\"tester\",\"platform\":[\"REMOTE_PLAY\"]},"
           + "\"body\":{\"data\":{"
           + "\"customProperties\":{\"from\":{\"accountId\":\"1\",\"platform\":\"" + fromPlatform + "\"}},"
           + "\"sessionId\":\"00000000-0000-0000-0000-000000000000\","
           + "\"sessionMessage\":{\"channel\":\"remote_play:1\","
           + "\"payload\":\"ver=1.0, type=text, body=" + innerBody + "\"}}}}";

    // Inner bodies are written with \" so that, once embedded in the payload string and the whole frame is
    // parsed as JSON, the payload value contains a real JSON document — exactly the double-nesting the
    // captured frames use. Addresses are placeholder (TEST-NET / RFC 1918); the skey/hashedId are arbitrary
    // valid-length base64.
    private static string ConsoleOffer => Frame("PROSPERO",
        "{\\\"action\\\":\\\"OFFER\\\",\\\"reqId\\\":1,\\\"error\\\":0,\\\"connRequest\\\":{\\\"sid\\\":16531,\\\"peerSid\\\":0,"
        + "\\\"skey\\\":\\\"3DlNoSVIIrMt0LZJUsljaQ==\\\",\\\"natType\\\":2,\\\"candidate\\\":["
        + "{\\\"type\\\":\\\"STATIC\\\",\\\"addr\\\":\\\"203.0.113.7\\\",\\\"mappedAddr\\\":\\\"0.0.0.0\\\",\\\"port\\\":9303,\\\"mappedPort\\\":0},"
        + "{\\\"type\\\":\\\"LOCAL\\\",\\\"addr\\\":\\\"192.168.1.50\\\",\\\"mappedAddr\\\":\\\"0.0.0.0\\\",\\\"port\\\":9303,\\\"mappedPort\\\":0}],"
        + "\\\"defaultRouteMacAddr\\\":\\\"00:11:22:33:44:55\\\","
        + "\\\"localPeerAddr\\\":{\\\"accountId\\\":\\\"1\\\",\\\"platform\\\":\\\"PROSPERO\\\"},"
        + "\\\"localHashedId\\\":\\\"kZ4dwoBU+W1wDpBeejemC+lZ1Eo=\\\"}}");

    [Fact]
    public void ParsesTheConsolesOfferAndItsCandidates()
    {
        HalyardSignalingMessage? msg = HalyardSignalingMessage.TryParse(ConsoleOffer);

        Assert.NotNull(msg);
        Assert.Equal("OFFER", msg!.Action);
        Assert.Equal(1, msg.ReqId);
        Assert.Equal("PROSPERO", msg.FromPlatform);
        Assert.True(msg.IsConsoleOffer);

        Assert.Collection(msg.Candidates,
            c => { Assert.Equal("STATIC", c.Type); Assert.Equal("203.0.113.7", c.Address); Assert.Equal(9303, c.Port); },
            c => { Assert.Equal("LOCAL", c.Type); Assert.Equal("192.168.1.50", c.Address); Assert.Equal(9303, c.Port); });
    }

    [Fact]
    public void RecoversTheSkeyAndHashedId()
    {
        // These are populated in the console's OFFER (16B / 20B), unlike the zeros our own OFFER sends — which
        // is the difference this decode surfaced and the send side will have to match.
        HalyardSignalingMessage msg = HalyardSignalingMessage.TryParse(ConsoleOffer)!;

        Assert.NotNull(msg.SessionKey);
        Assert.Equal(16, msg.SessionKey!.Length);
        Assert.NotNull(msg.LocalHashedId);
        Assert.Equal(20, msg.LocalHashedId!.Length);
    }

    [Fact]
    public void OurOwnEchoedOfferIsNotMistakenForTheConsoles()
    {
        // The push channel notifies us about our own posted messages too. Acting on those as if they were the
        // console's candidates would point the transport back at ourselves.
        string ownOffer = ConsoleOffer.Replace("\"platform\":\"PROSPERO\"", "\"platform\":\"REMOTE_PLAY\"");

        HalyardSignalingMessage msg = HalyardSignalingMessage.TryParse(ownOffer)!;

        Assert.Equal("REMOTE_PLAY", msg.FromPlatform);
        Assert.False(msg.IsConsoleOffer);
    }

    [Fact]
    public void ResultAcknowledgementHasNoCandidates()
    {
        string result = Frame("PROSPERO",
            "{\\\"action\\\":\\\"RESULT\\\",\\\"reqId\\\":1,\\\"error\\\":0,\\\"connRequest\\\":{}}");

        HalyardSignalingMessage msg = HalyardSignalingMessage.TryParse(result)!;

        Assert.Equal("RESULT", msg.Action);
        Assert.Empty(msg.Candidates);
        Assert.False(msg.IsConsoleOffer);
    }

    [Theory]
    [InlineData("members:created")]
    [InlineData("customData1:updated")]
    [InlineData("remotePlaySession:created")]
    public void NonSignalingNotificationsAreNotSignalingMessages(string dataType)
    {
        // Most push frames are presence/membership notifications. They must parse to null, not throw and not be
        // mistaken for a signaling message.
        string frame = Frame("PROSPERO", "{}", dataType);

        Assert.Null(HalyardSignalingMessage.TryParse(frame));
    }

    [Fact]
    public void StunCandidateFromPs4IsParsed()
    {
        // The PS4 OFFER additionally advertised a STUN candidate; an unfamiliar/extra type must come through
        // rather than being dropped or throwing.
        string ps4 = Frame("PS4",
            "{\\\"action\\\":\\\"OFFER\\\",\\\"reqId\\\":1,\\\"error\\\":0,\\\"connRequest\\\":{\\\"sid\\\":1,\\\"peerSid\\\":0,"
            + "\\\"skey\\\":\\\"3DlNoSVIIrMt0LZJUsljaQ==\\\",\\\"natType\\\":2,\\\"candidate\\\":["
            + "{\\\"type\\\":\\\"STUN\\\",\\\"addr\\\":\\\"203.0.113.9\\\",\\\"mappedAddr\\\":\\\"0.0.0.0\\\",\\\"port\\\":1029,\\\"mappedPort\\\":0}],"
            + "\\\"localHashedId\\\":\\\"kZ4dwoBU+W1wDpBeejemC+lZ1Eo=\\\"}}");

        HalyardSignalingMessage msg = HalyardSignalingMessage.TryParse(ps4)!;

        HalyardSignalingCandidate stun = Assert.Single(msg.Candidates);
        Assert.Equal("STUN", stun.Type);
        Assert.Equal(1029, stun.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"dataType\":\"psn:sessionManager:sys:rps:sessionMessage:created\"}")]
    public void MalformedOrIncompleteFramesReturnNull(string frame)
        => Assert.Null(HalyardSignalingMessage.TryParse(frame));
}
