using System.Buffers.Binary;
using System.Net;
using Ripcord.Core.Net;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The STUN Binding Response parser. Worth pinning because the address it recovers is the one a distant
/// console is told to send to: get it wrong and nothing arrives, with no error anywhere to say why.
/// </summary>
public class StunReflexiveAddressTests
{
    private const uint MagicCookie = 0x2112A442;
    private static readonly byte[] TransactionId = [.. Enumerable.Range(1, 12).Select(i => (byte)i)];

    /// <summary>Build a Binding Response carrying XOR-MAPPED-ADDRESS, optionally preceded by other attributes.</summary>
    private static byte[] Response(
        IPEndPoint mapped,
        byte[]? transactionId = null,
        ushort messageType = 0x0101,
        uint cookie = MagicCookie,
        params (ushort Type, byte[] Value)[] before)
    {
        var attributes = new List<byte>();

        void Append(ushort type, byte[] value)
        {
            var header = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(header, type);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)value.Length);
            attributes.AddRange(header);
            attributes.AddRange(value);
            // Attributes are padded to a 4-byte boundary, and the padding is not counted in the length.
            attributes.AddRange(new byte[((value.Length + 3) & ~3) - value.Length]);
        }

        foreach ((ushort type, byte[] value) in before)
        {
            Append(type, value);
        }

        var xor = new byte[8];
        xor[1] = 0x01; // IPv4
        BinaryPrimitives.WriteUInt16BigEndian(xor.AsSpan(2), (ushort)(mapped.Port ^ (MagicCookie >> 16)));
        uint address = BinaryPrimitives.ReadUInt32BigEndian(mapped.Address.GetAddressBytes()) ^ MagicCookie;
        BinaryPrimitives.WriteUInt32BigEndian(xor.AsSpan(4), address);
        Append(0x0020, xor);

        var message = new byte[20 + attributes.Count];
        BinaryPrimitives.WriteUInt16BigEndian(message, messageType);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)attributes.Count);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), cookie);
        (transactionId ?? TransactionId).CopyTo(message, 8);
        attributes.CopyTo(message, 20);
        return message;
    }

    [Fact]
    public void RecoversTheMappedEndpoint()
    {
        var expected = new IPEndPoint(IPAddress.Parse("198.51.100.55"), 3492);
        Assert.Equal(expected, StunReflexiveAddress.TryParseMapped(Response(expected), TransactionId));
    }

    [Fact]
    public void SkipsPrecedingAttributes_IncludingOnesNeedingPadding()
    {
        // Real responses put other attributes first, and a 3-byte one pads to 4 without saying so in its
        // length -- walk that wrongly and every attribute after it is misread.
        var expected = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 61000);
        byte[] message = Response(
            expected,
            before: [(0x0004, [0, 1, 0x0d, 0x96, 52, 40, 62, 100]), (0x8022, [0x61, 0x62, 0x63])]);

        Assert.Equal(expected, StunReflexiveAddress.TryParseMapped(message, TransactionId));
    }

    [Fact]
    public void RejectsAResponseToSomebodyElsesRequest()
    {
        // The transaction id is the only thing tying a datagram to our request, and this socket carries the
        // real session traffic too.
        byte[] other = [.. Enumerable.Range(50, 12).Select(i => (byte)i)];
        byte[] message = Response(new IPEndPoint(IPAddress.Loopback, 1234), transactionId: other);

        Assert.Null(StunReflexiveAddress.TryParseMapped(message, TransactionId));
    }

    [Theory]
    [InlineData((ushort)0x0111, MagicCookie)]   // an error response, not a binding response
    [InlineData((ushort)0x0101, 0xDEADBEEF)]    // not STUN at all
    public void RejectsWhatIsNotABindingResponse(ushort messageType, uint cookie)
        => Assert.Null(StunReflexiveAddress.TryParseMapped(
            Response(new IPEndPoint(IPAddress.Loopback, 1234), messageType: messageType, cookie: cookie),
            TransactionId));

    [Fact]
    public void RejectsATruncatedMessage()
        => Assert.Null(StunReflexiveAddress.TryParseMapped([0x01, 0x01, 0x00], TransactionId));
}
