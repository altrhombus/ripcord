using Ripcord.Protocol.Halyard.Common.Control;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The persistent /sess/ctrl binary framing (8-byte header: u32 payload length, u16 type, u16 zero),
/// pinned to bytes lifted from a real console's ctrl stream. Answering HEARTBEAT_REQ keeps the session
/// alive — without it the console RSTs the whole session shortly after A/V begins.
/// </summary>
public class HalyardCtrlMessageTests
{
    [Fact]
    public void Parse_RealHeartbeatRequest()
    {
        // Wire bytes (console -> client): size=0, type=0x00fe, reserved=0.
        byte[] wire = Convert.FromHexString("0000000000fe0000");

        Assert.True(HalyardCtrlMessage.TryParse(wire, out var message, out int consumed));
        Assert.Equal(HalyardCtrlMessage.TypeHeartbeatReq, message.Type);
        Assert.Equal(0, message.Payload.Length);
        Assert.Equal(8, consumed);
    }

    [Fact]
    public void HeartbeatReply_SerializesToExactWireBytes()
    {
        // The reply the vendor client sends back for every request (client -> console): type 0x01fe, empty.
        var reply = new HalyardCtrlMessage(HalyardCtrlMessage.TypeHeartbeatRep);
        Assert.Equal("0000000001fe0000", Convert.ToHexString(reply.Serialize()).ToLowerInvariant());
    }

    [Fact]
    public void Parse_MessageWithPayload_AndAdvancesForNextFrame()
    {
        // A session-id frame (size=0x11, type=0x0033, 17-byte payload) immediately followed by a heartbeat
        // request — the reader must consume exactly the first frame and leave the second intact.
        // Synthetic: 0x10 then ASCII "SyntheticSessId!" - the real frame's shape (a length prefix and
        // sixteen bytes of id) with content that decodes to something self-evidently invented.
        //
        // The value here until 2026-09-11 carried this same "Nothing captured" line and it was false.
        // It was the payload of a real 0x0033 frame, lifted from a capture with its 00000011 00330000
        // header still attached. The console encrypts that frame, so the committed bytes were
        // ciphertext; against the plaintext docs/protocol/ps5-session-transport.md publishes for it,
        // they gave up 17 bytes of session keystream. Three audit rounds cleared it by reading the
        // comment - which is the point: a false declaration is stronger than no declaration, because
        // it is where the reviewer stops. Provenance is now checked, not asserted; see
        // CaptureProvenanceTests.NoCommittedLiteral_IsInACapture.
        byte[] payload = Convert.FromHexString("1053796e74686574696353657373496421");
        byte[] wire = Convert.FromHexString("0000001100330000" + Convert.ToHexString(payload) + "0000000000fe0000");

        Assert.True(HalyardCtrlMessage.TryParse(wire, out var first, out int consumed));
        Assert.Equal(HalyardCtrlMessage.TypeSessionId, first.Type);
        Assert.Equal(payload, first.Payload.ToArray());
        Assert.Equal(8 + 17, consumed);

        Assert.True(HalyardCtrlMessage.TryParse(wire.AsSpan(consumed), out var second, out _));
        Assert.Equal(HalyardCtrlMessage.TypeHeartbeatReq, second.Type);
    }

    [Fact]
    public void TryParse_IncompleteFrame_ReturnsFalse()
    {
        // Header says 17-byte payload but only 4 bytes present — wait for more.
        byte[] partial = Convert.FromHexString("00000011003300001122334455");
        Assert.False(HalyardCtrlMessage.TryParse(partial, out _, out _));
    }
}
