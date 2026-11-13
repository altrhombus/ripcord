using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The RPCS inner-message layer: the <c>be64 seq | be32 payload_len | be16 cmd | be16 flags | payload</c> format
/// (<see cref="HalyardRpcsMessage"/>), the confirmed command payloads (<see cref="HalyardRpcsCommands"/>), and the
/// message-level <see cref="HalyardRpcsChannel.SealMessage"/>/<see cref="HalyardRpcsChannel.OpenMessage"/> that
/// bind the inner <c>seq</c> to the frame's GCM counter. Format + command numbers + payloads are confirmed from
/// session 004's own decrypt (RPCS-SEMANTICS.md); the GCM is stubbed here (KAT'd elsewhere).
/// </summary>
public class V2RpcsMessageTests
{
    [Fact]
    public void Message_EncodeDecode_RoundTrips()
    {
        var message = new HalyardRpcsMessage(Seq: 7, Cmd: 0x0033, Flags: 0, Payload: [0x10, 0xAA, 0xBB]);
        Assert.True(HalyardRpcsMessage.TryDecode(message.Encode(), out HalyardRpcsMessage back));
        Assert.Equal(7ul, back.Seq);
        Assert.Equal(0x0033, back.Cmd);
        Assert.Equal(0, back.Flags);
        Assert.Equal(new byte[] { 0x10, 0xAA, 0xBB }, back.Payload);
    }

    /// <summary>
    /// The probe report as session 004 sent it (out seq 1, cmd 0x000d): the exact inner plaintext. A golden vector
    /// pinning both the inner format and the probe payload against our own capture.
    /// </summary>
    [Fact]
    public void ProbeReport_Seq1_MatchesTheCapturedInnerBytes()
    {
        var message = new HalyardRpcsMessage(
            Seq: 1, Cmd: HalyardRpcsCommands.ProbeReport, Flags: 0,
            Payload: HalyardRpcsCommands.BuildProbeReportPayload()); // default MTU 1454 = 0x05ae

        byte[] expected = Convert.FromHexString(
            "0000000000000001" + // be64 seq = 1
            "00000010" +         // be32 payload_len = 16
            "000d" +             // be16 cmd = 0x000d
            "0000" +             // be16 flags = 0
            "000000000000" + "05ae" + "0000000000000000"); // payload: MTU 0x05ae at offset 6, rest zero

        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(message.Encode()));
    }

    [Fact]
    public void Keepalive_HasTheCapturedPayload()
    {
        Assert.Equal(new byte[] { 0x00, 0x01, 0x01, 0x00 }, HalyardRpcsCommands.KeepalivePayload.ToArray());
        Assert.Equal(0x0036, HalyardRpcsCommands.Keepalive);
    }

    [Fact]
    public void ReplyBit_DistinguishesRequestFromReply()
    {
        Assert.Equal(HalyardRpcsCommands.EchoRequest | HalyardRpcsCommands.ReplyBit, HalyardRpcsCommands.EchoReply);
        Assert.True(new HalyardRpcsMessage(1, HalyardRpcsCommands.EchoReply, 0, []).IsReply);
        Assert.False(new HalyardRpcsMessage(1, HalyardRpcsCommands.EchoRequest, 0, []).IsReply);
    }

    [Theory]
    [InlineData(1454, "05ae")]
    [InlineData(1500, "05dc")]
    public void BuildProbeReportPayload_PutsTheMtuAtOffset6(int mtu, string hiLo)
    {
        byte[] payload = HalyardRpcsCommands.BuildProbeReportPayload((ushort)mtu);
        Assert.Equal(16, payload.Length);
        Assert.Equal(hiLo, Convert.ToHexString(payload.AsSpan(6, 2)).ToLowerInvariant());
        Assert.Equal("000000000000", Convert.ToHexString(payload.AsSpan(0, 6)).ToLowerInvariant()); // leading zeros
    }

    [Fact]
    public void SealMessage_BindsSeqToTheSendCounter_AndOpenMessageRecoversIt()
    {
        var sender = new HalyardRpcsChannel(new FakeV2ControlCrypto());
        var receiver = new HalyardRpcsChannel(new FakeV2ControlCrypto());

        // First message: seq 1 (continuing from ctrl's 0).
        byte[] frame1 = sender.SealMessage(HalyardRpcsCommands.ProbeReport, HalyardRpcsCommands.BuildProbeReportPayload());
        HalyardRpcsMessage? m1 = receiver.OpenMessage(frame1);
        Assert.NotNull(m1);
        Assert.Equal(1ul, m1!.Value.Seq);
        Assert.Equal(HalyardRpcsCommands.ProbeReport, m1.Value.Cmd);
        Assert.Equal(HalyardRpcsCommands.BuildProbeReportPayload(), m1.Value.Payload);

        // Second message: seq 2, and the keepalive body survives the round-trip.
        byte[] frame2 = sender.SealMessage(HalyardRpcsCommands.Keepalive, HalyardRpcsCommands.KeepalivePayload);
        HalyardRpcsMessage? m2 = receiver.OpenMessage(frame2);
        Assert.NotNull(m2);
        Assert.Equal(2ul, m2!.Value.Seq);
        Assert.Equal(HalyardRpcsCommands.Keepalive, m2.Value.Cmd);
        Assert.Equal(HalyardRpcsCommands.KeepalivePayload.ToArray(), m2.Value.Payload);
    }

    [Fact]
    public void OpenMessage_ReturnsNull_OnATamperedFrame()
    {
        var sender = new HalyardRpcsChannel(new FakeV2ControlCrypto());
        var receiver = new HalyardRpcsChannel(new FakeV2ControlCrypto());

        byte[] frame = sender.SealMessage(HalyardRpcsCommands.Keepalive, HalyardRpcsCommands.KeepalivePayload);
        frame[^1] ^= 0xFF;
        Assert.Null(receiver.OpenMessage(frame));
        Assert.Equal(1ul, receiver.RecvCounter); // untouched
    }

    [Fact]
    public void TryDecode_RejectsALengthMismatch()
    {
        // Declared payload_len (0xFF) exceeds the bytes present.
        byte[] inner = Convert.FromHexString("0000000000000001" + "000000ff" + "0036" + "0000" + "00010100");
        Assert.False(HalyardRpcsMessage.TryDecode(inner, out _));
    }
}
