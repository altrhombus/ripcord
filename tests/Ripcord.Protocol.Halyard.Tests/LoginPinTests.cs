using System.Text;
using Ripcord.Protocol.Halyard.Common.Control;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The console login-passcode encoding and framing, pinned to cap50 (2026-08-03). The end-to-end encryption
/// is validated against a real captured payload in <see cref="LiveStreamPacketVectorTests"/>-style fixtures
/// when present; here we pin the parts that need no keys — the plaintext shape, the counter, and the frame.
/// </summary>
public class LoginPinTests
{
    [Fact]
    public void PinPlaintext_IsTheAsciiDigits()
    {
        // The digits go out as their ASCII bytes and nothing else; the field cipher is a stream mode, so a
        // 4-digit passcode also ciphertexts to 4 bytes. The passcode here is synthetic — the captured one is
        // a real account credential and stays in the dirty room.
        byte[] plaintext = HalyardSessCtrlFields.BuildLoginPinPlaintext("1234");
        Assert.Equal(new byte[] { 0x31, 0x32, 0x33, 0x34 }, plaintext);
        Assert.Equal("1234", Encoding.ASCII.GetString(plaintext));
    }

    [Theory]
    [InlineData("12a4")]
    [InlineData("12 4")]
    [InlineData("")]
    public void PinPlaintext_RejectsNonDigits(string bad)
    {
        Assert.ThrowsAny<ArgumentException>(() => HalyardSessCtrlFields.BuildLoginPinPlaintext(bad));
    }

    [Fact]
    public void LoginPinCounter_ContinuesPastTheCtrlFields()
    {
        // The five /sess/ctrl fields are counters 0..4; the passcode, on the same connection, is 5. cap50's
        // payload only decrypts to the typed digits at counter 5, so this value is load-bearing, not cosmetic.
        Assert.Equal(5ul, HalyardSessCtrlFields.CounterLoginPin);
        Assert.Equal(4ul, HalyardSessCtrlFields.CounterStreamingType);
    }

    [Fact]
    public void LoginSubmitFrame_RoundTrips()
    {
        // A 0x8004 frame wrapping a 4-byte ciphertext, as sent on the binary control channel. The framing
        // treats the payload as opaque, so a synthetic ciphertext pins it exactly as well as the captured one
        // (which is a real passcode under a real session key, and so stays in the dirty room).
        byte[] ciphertext = { 0xde, 0xad, 0xbe, 0xef };
        var message = new HalyardCtrlMessage(HalyardCtrlMessage.TypeLoginSubmit, ciphertext);
        byte[] wire = message.Serialize();

        // [u32 len=4][u16 type=0x8004][u16 flags=0][payload]
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x04, 0x80, 0x04, 0x00, 0x00, 0xde, 0xad, 0xbe, 0xef }, wire);

        Assert.True(HalyardCtrlMessage.TryParse(wire, out var parsed, out int consumed));
        Assert.Equal(HalyardCtrlMessage.TypeLoginSubmit, parsed.Type);
        Assert.Equal(ciphertext, parsed.Payload.ToArray());
        Assert.Equal(wire.Length, consumed);
    }

    [Fact]
    public void LoginPromptFrame_HasNoPayload()
    {
        // The console's 0x0004 prompt is empty, as captured (0000000000040000).
        byte[] wire = new HalyardCtrlMessage(HalyardCtrlMessage.TypeLoginPrompt).Serialize();
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00 }, wire);
    }
}
