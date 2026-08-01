using Avstream;
using Google.Protobuf;
using Ripcord.Protocol.Halyard.Takion;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Tests for the reliable-delivery framing: the SACK chunk (validated byte-for-byte against a captured
/// SACK) and multi-chunk message reassembly (the fragment/ending-bit behaviour observed for the large
/// SESSION_REQUEST).
/// </summary>
public class TakionReliabilityTests
{
    [Fact]
    public void Sack_ReproducesCapturedChunk()
    {
        // Captured PS5->client SACK acking the client's DATA seq 0x4823 (a_rwnd 0x19000, no gap/dup blocks).
        const string captured = "0000004823000000000000000003000010000048230001900000000000";
        byte[] sack = TakionSackChunk.Build(verificationTag: 0x00004823, cumulativeTsnAck: 0x00004823);
        Assert.Equal(captured, Hex.String(sack));
    }

    [Fact]
    public void Sack_ParsesCapturedChunk()
    {
        byte[] captured = Hex.Bytes("0000b18ccf00000000000000000300001000b18cd00001900000000000");
        Assert.True(TakionSackChunk.TryParse(captured, out var parsed));
        Assert.Equal(0x00b18cd0u, parsed.CumulativeTsnAck);
        Assert.Equal(0x00019000u, parsed.ReceiveWindow);
        Assert.Equal(0, parsed.GapAckBlocks);
        Assert.Equal(0, parsed.DupTsns);
    }

    [Fact]
    public void Reassembler_SingleChunkMessage_DeliveredImmediately()
    {
        var reassembler = new TakionMessageReassembler();
        byte[] body = new ControlMessage { Type = ControlMessage.Types.MessageType.Heartbeat }.ToByteArray();

        // A real single-chunk DATA packet (first fragment, ends the message).
        byte[] packet = TakionDataChunk.Build(0x00b18ccf, seq: 1, channel: 0, body);
        Assert.True(TakionDataChunk.TryParse(packet, out var chunk));
        byte[]? complete = reassembler.Accept(chunk);

        Assert.NotNull(complete);
        Assert.Equal(Hex.String(body), Hex.String(complete!));
    }

    [Fact]
    public void Reassembler_TwoFragments_ConcatenatedOnEndingBit()
    {
        // Build a large SESSION_REQUEST, split its protobuf across two DATA chunks (first fragment at payload
        // offset 9, continuation at offset 8, ending bit only on the second), and confirm reassembly reproduces
        // the original message through the real wire framing.
        var message = new ControlMessage
        {
            Type = ControlMessage.Types.MessageType.SessionRequest,
            SessionRequestPayload = new SessionRequestPayload
            {
                ClientVersion = 9,
                SessionKey = "session",
                LaunchSpecJson = new string('x', 1500), // forces a realistic multi-chunk size
            },
        };
        byte[] body = message.ToByteArray();
        int split = body.Length / 2;

        byte[] frag1 = TakionDataChunk.Build(0x00b18ccf, seq: 10, channel: TakionDataChunk.ChannelSession, body.AsSpan(0, split), endOfMessage: false, firstFragment: true);
        byte[] frag2 = TakionDataChunk.Build(0x00b18ccf, seq: 11, channel: TakionDataChunk.ChannelSession, body.AsSpan(split), endOfMessage: true, firstFragment: false);

        var reassembler = new TakionMessageReassembler();
        Assert.True(TakionDataChunk.TryParse(frag1, out var p1));
        Assert.Null(reassembler.Accept(p1));
        Assert.True(TakionDataChunk.TryParse(frag2, out var p2));
        byte[]? complete = reassembler.Accept(p2);

        Assert.NotNull(complete);
        Assert.Equal(message, ControlMessage.Parser.ParseFrom(complete));
    }

    [Fact]
    public void Reassembler_FragmentsRoundTripThroughDataChunks()
    {
        // End-to-end through the wire framing: build two DATA packets (non-final + final), parse, reassemble.
        var message = new ControlMessage
        {
            Type = ControlMessage.Types.MessageType.SessionReply,
            SessionRequestPayload = new SessionRequestPayload { ClientVersion = 9, SessionKey = "hello-world-1234" },
        };
        byte[] body = message.ToByteArray();
        int split = body.Length / 2;

        byte[] frag1 = TakionDataChunk.Build(0x00b18ccf, seq: 20, channel: TakionDataChunk.ChannelSession, body.AsSpan(0, split), endOfMessage: false, firstFragment: true);
        byte[] frag2 = TakionDataChunk.Build(0x00b18ccf, seq: 21, channel: TakionDataChunk.ChannelSession, body.AsSpan(split), endOfMessage: true, firstFragment: false);

        var reassembler = new TakionMessageReassembler();
        Assert.True(TakionDataChunk.TryParse(frag1, out var p1));
        Assert.False(p1.EndOfMessage);
        Assert.Null(reassembler.Accept(p1));

        Assert.True(TakionDataChunk.TryParse(frag2, out var p2));
        Assert.True(p2.EndOfMessage);
        byte[]? complete = reassembler.Accept(p2);

        Assert.NotNull(complete);
        Assert.Equal(message, ControlMessage.Parser.ParseFrom(complete));
    }
}
