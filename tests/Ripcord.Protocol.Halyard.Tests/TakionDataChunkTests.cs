using Avstream;
using Google.Protobuf;
using Ripcord.Protocol.Halyard.Takion;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Validates the DATA chunk framing against real captured control packets (structural handshake traffic:
/// version negotiation, bandwidth probe, disconnect). Each parses into the expected <c>ControlMessage</c>
/// type at the pinned protobuf offset, and a build round-trip reproduces the wire bytes.
/// </summary>
public class TakionDataChunkTests
{
    [Theory]
    // (hex, expected channel, expected message type)
    [InlineData("0000b18ccf000000000000000000010014000048230015000000081ffa01020809",
        TakionDataChunk.ChannelProtocolVersion, ControlMessage.Types.MessageType.ProtocolVersionRequest)]
    [InlineData("000000482300000000000000000001001400b18ccf000000000008208202020809",
        0x0000, ControlMessage.Types.MessageType.ProtocolVersionAck)]
    [InlineData("0000b18ccf000000000000000000010017000048250008000000080c7206080012020801",
        TakionDataChunk.ChannelBandwidth, ControlMessage.Types.MessageType.BandwidthProbe)]
    public void Parse_RealDataPacket_YieldsExpectedControlMessage(string hex, int expectedChannel, ControlMessage.Types.MessageType expectedType)
    {
        byte[] packet = Hex.Bytes(hex);

        Assert.True(TakionDataChunk.TryParse(packet, out var parsed));
        Assert.Equal((ushort)expectedChannel, parsed.Channel);
        Assert.NotEqual(0u, parsed.Seq);
        Assert.True(parsed.EndOfMessage); // these are all single-chunk messages

        var message = ControlMessage.Parser.ParseFrom(parsed.Payload.Span);
        Assert.Equal(expectedType, message.Type);
    }

    [Fact]
    public void Build_ReproducesCapturedVersionRequest()
    {
        // Reconstruct the ProtocolVersionRequest DATA packet (seq 0x4823, channel 0x15) from its
        // ControlMessage and confirm it is byte-for-byte the captured packet.
        const string captured = "0000b18ccf000000000000000000010014000048230015000000081ffa01020809";
        byte[] protobuf = Hex.Bytes("081ffa01020809");

        byte[] built = TakionDataChunk.Build(
            verificationTag: 0x00b18ccf,
            seq: 0x00004823,
            channel: TakionDataChunk.ChannelProtocolVersion,
            protobuf);

        Assert.Equal(captured, Hex.String(built));
    }

    [Fact]
    public void Build_Parse_RoundTripsAControlMessage()
    {
        var message = new ControlMessage
        {
            Type = ControlMessage.Types.MessageType.SessionRequest,
            SessionRequestPayload = new SessionRequestPayload
            {
                ClientVersion = 9,
                SessionKey = "k",
                LaunchSpecJson = "{}",
            },
        };
        byte[] body = message.ToByteArray();

        byte[] packet = TakionDataChunk.Build(0x00b18ccf, seq: 0x00004824, channel: TakionDataChunk.ChannelSession, body);

        Assert.True(TakionDataChunk.TryParse(packet, out var parsed));
        Assert.Equal(0x00004824u, parsed.Seq);
        Assert.Equal(TakionDataChunk.ChannelSession, parsed.Channel);
        Assert.Equal(message, ControlMessage.Parser.ParseFrom(parsed.Payload.Span));
    }
}
