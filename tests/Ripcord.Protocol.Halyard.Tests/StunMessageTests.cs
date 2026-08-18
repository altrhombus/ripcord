using System.Net;
using Ripcord.Core.Net.Stun;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The STUN message codec, validated against the canonical RFC 5769 test vector for XOR-MAPPED-ADDRESS.
///
/// <para>
/// RFC 5769 exists precisely so a STUN implementation can be checked without a live server: its sample
/// response decodes to a known address, and getting that exact value out is proof the XOR arithmetic (port
/// against the cookie's high half, address against the whole cookie) is right rather than merely
/// plausible-looking. This is the same "validate against a published vector" discipline the crypto KDFs use.
/// </para>
/// </summary>
public class StunMessageTests
{
    // RFC 5769 §2.2 "Sample IPv4 Response": transaction id b7e7a701 bc34d686 fa87dfae, one XOR-MAPPED-ADDRESS
    // attribute encoding 192.0.2.1:32853. Reduced to header + that one attribute.
    private static readonly byte[] Rfc5769Response = Convert.FromHexString(
        "0101000c" +                    // Binding Success, 12 bytes of attributes
        "2112a442" +                    // magic cookie
        "b7e7a701bc34d686fa87dfae" +    // transaction id
        "00200008" +                    // XOR-MAPPED-ADDRESS, length 8
        "0001a147e112a643");            // reserved/family=IPv4, X-port, X-address

    [Fact]
    public void DecodesTheRfc5769XorMappedAddressVector()
    {
        StunMessage? msg = StunMessage.TryParse(Rfc5769Response);

        Assert.NotNull(msg);
        Assert.Equal(StunMessageType.BindingSuccess, msg!.Type);
        Assert.NotNull(msg.MappedAddress);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), msg.MappedAddress!.Address);
        Assert.Equal(32853, msg.MappedAddress.Port);
    }

    [Fact]
    public void DecodesPlainMappedAddress()
    {
        // The RFC 3489 form some servers still return — same address, not XORed.
        byte[] response = Convert.FromHexString(
            "0101000c" + "2112a442" + "b7e7a701bc34d686fa87dfae" +
            "00010008" + "00018055c0000201"); // MAPPED-ADDRESS: family IPv4, port 0x8055=32853, 192.0.2.1

        StunMessage msg = StunMessage.TryParse(response)!;

        Assert.Equal(IPAddress.Parse("192.0.2.1"), msg.MappedAddress!.Address);
        Assert.Equal(32853, msg.MappedAddress.Port);
    }

    [Fact]
    public void DecodesTheLegacyXorMappedAddressAttribute()
    {
        // 0x8020 (comprehension-optional) is the pre-standardization XOR form, and is what the vendor's own
        // relay returns — it must decode identically to 0x0020.
        byte[] response = Convert.FromHexString(
            "0101000c" + "2112a442" + "b7e7a701bc34d686fa87dfae" +
            "80200008" + "0001a147e112a643");

        StunMessage msg = StunMessage.TryParse(response)!;

        Assert.Equal(IPAddress.Parse("192.0.2.1"), msg.MappedAddress!.Address);
        Assert.Equal(32853, msg.MappedAddress.Port);
    }

    [Fact]
    public void SkipsUnknownAttributesAndStillFindsTheAddress()
    {
        // A real response also carries SOURCE-ADDRESS, MESSAGE-INTEGRITY, etc. An unknown or unhandled
        // attribute must be stepped over (respecting 4-byte padding), not fatal.
        byte[] response = Convert.FromHexString(
            "01010014" + "2112a442" + "b7e7a701bc34d686fa87dfae" +   // 0x14 = 20 bytes of attributes
            "80220003" + "414243" + "00" +          // SOFTWARE "ABC" (3 bytes + 1 pad) = 8 bytes
            "00200008" + "0001a147e112a643");        // XOR-MAPPED-ADDRESS = 12 bytes

        StunMessage msg = StunMessage.TryParse(response)!;

        Assert.Equal(IPAddress.Parse("192.0.2.1"), msg.MappedAddress!.Address);
        Assert.Equal(32853, msg.MappedAddress.Port);
    }

    [Fact]
    public void BindingRequest_RoundTrips()
    {
        StunMessage request = StunMessage.CreateBindingRequest();
        byte[] bytes = request.ToBytes();

        Assert.Equal(20, bytes.Length); // header only, no attributes

        StunMessage parsed = StunMessage.TryParse(bytes)!;
        Assert.Equal(StunMessageType.BindingRequest, parsed.Type);
        Assert.Equal(request.TransactionId, parsed.TransactionId);
        Assert.Null(parsed.MappedAddress);
    }

    [Fact]
    public void FreshRequestsHaveDistinctTransactionIds()
    {
        // The transaction id is how a response is matched to its request; a repeated id would let a stale reply
        // satisfy the wrong attempt.
        Assert.NotEqual(
            StunMessage.CreateBindingRequest().TransactionId,
            StunMessage.CreateBindingRequest().TransactionId);
    }

    [Theory]
    [InlineData("")]                                   // empty
    [InlineData("00010000")]                           // too short for a header
    [InlineData("0001000000000000b7e7a701bc34d686fa87dfae")] // valid length but wrong cookie -> not STUN
    public void NonStunDatagramsReturnNull(string hex)
        => Assert.Null(StunMessage.TryParse(Convert.FromHexString(hex)));

    [Fact]
    public void AttributeRunningPastTheEndDoesNotThrow()
    {
        // The declared attributes region is 8 bytes, but the attribute inside it claims an 8-byte value with
        // only 4 bytes to hold it. A hostile or corrupt packet must not crash the receive loop; the address is
        // simply unreadable.
        byte[] malformed = Convert.FromHexString(
            "01010008" + "2112a442" + "b7e7a701bc34d686fa87dfae" + "00200008" + "00010001");

        StunMessage msg = StunMessage.TryParse(malformed)!;
        Assert.Null(msg.MappedAddress);
    }
}
