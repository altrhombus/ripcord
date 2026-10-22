using System.Buffers.Binary;
using System.Security.Cryptography;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The v2 RPCS during-stream channel framing (<see cref="HalyardRpcsChannel"/>): the
/// <c>"RPCS"‖be32(inner_len+16)‖ct‖tag16</c> wire shape, the 8-byte-header AAD, and the per-direction counter
/// continuing from ctrl's 0 (so the first message is counter 1). These isolate the <b>framing</b> from the GCM
/// envelope — the envelope's byte-correctness is KAT'd separately (<see cref="V2ControlHandshakeTests"/>,
/// <c>V2StreamPacketVectorTests</c>) — by backing the channel with a deterministic AEAD double, so a round-trip
/// proves the header, length field, AAD binding and counter discipline rather than re-testing AES-GCM.
/// </summary>
public class V2RpcsChannelTests
{
    [Fact]
    public void Seal_ProducesTheRpcsWireShape()
    {
        var channel = new HalyardRpcsChannel(new FakeControlCrypto());
        byte[] inner = [1, 2, 3, 4, 5];
        byte[] frame = channel.Seal(inner);

        Assert.Equal("RPCS"u8.ToArray(), frame[..4]);
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4, 4));
        Assert.Equal((uint)(inner.Length + HalyardRpcsChannel.TagLength), declared);         // ct + tag16
        Assert.Equal(HalyardRpcsChannel.HeaderLength + inner.Length + HalyardRpcsChannel.TagLength, frame.Length);
    }

    [Fact]
    public void RoundTrips_InOrder_WithCountersContinuingFromCtrl()
    {
        var sender = new HalyardRpcsChannel(new FakeControlCrypto());   // client: seals sk_0x09
        var receiver = new HalyardRpcsChannel(new FakeControlCrypto()); // peer deriving the same keys

        // The first message in each direction is counter 1 (ctrl was 0 on this connection).
        Assert.Equal(1ul, sender.SendCounter);
        Assert.Equal(1ul, receiver.RecvCounter);

        byte[][] messages =
        [
            "0036:00 01 01 00 (keepalive)"u8.ToArray(),
            "000d:probe report"u8.ToArray(),
            []  // an empty inner is still a valid frame (header + tag only)
        ];

        for (int i = 0; i < messages.Length; i++)
        {
            byte[] frame = sender.Seal(messages[i]);
            byte[]? opened = receiver.Open(frame);
            Assert.NotNull(opened);
            Assert.Equal(messages[i], opened);
            Assert.Equal((ulong)(i + 2), sender.SendCounter);   // advanced past this message
            Assert.Equal((ulong)(i + 2), receiver.RecvCounter);
        }
    }

    [Fact]
    public void Open_WithTheWrongCounterOrder_FailsAndLeavesTheCounterUntouched()
    {
        var sender = new HalyardRpcsChannel(new FakeControlCrypto());
        var receiver = new HalyardRpcsChannel(new FakeControlCrypto());

        byte[] first = sender.Seal("a"u8);     // sealed at counter 1
        byte[] second = sender.Seal("b"u8);    // sealed at counter 2

        // Delivering the second frame first authenticates against counter 1 (wrong) and must fail without
        // advancing — the AAD/counter binding is what the double reproduces.
        Assert.Null(receiver.Open(second));
        Assert.Equal(1ul, receiver.RecvCounter);

        // In order, both still open.
        Assert.NotNull(receiver.Open(first));
        Assert.NotNull(receiver.Open(second));
    }

    [Fact]
    public void Open_RejectsATamperedTag_WithoutAdvancing()
    {
        var sender = new HalyardRpcsChannel(new FakeControlCrypto());
        var receiver = new HalyardRpcsChannel(new FakeControlCrypto());

        byte[] frame = sender.Seal("payload"u8);
        frame[^1] ^= 0xFF; // corrupt the last tag byte

        Assert.Null(receiver.Open(frame));
        Assert.Equal(1ul, receiver.RecvCounter);
    }

    [Theory]
    [InlineData(new byte[] { 0x51, 0x50, 0x43, 0x53 }, false)]                 // "QPCS" — wrong magic
    [InlineData(new byte[] { 0x52, 0x50, 0x43, 0x53, 0, 0, 0, 0 }, false)]     // declared 0 < tag16
    [InlineData(new byte[] { 0x52, 0x50, 0x43, 0x53, 0, 0, 0, 0x10 }, true)]   // declared 16 (bare tag)
    public void TryGetFrameLength_ValidatesTheHeader(byte[] header, bool expected)
    {
        bool ok = HalyardRpcsChannel.TryGetFrameLength(header, out int frameLength);
        Assert.Equal(expected, ok);
        if (ok)
            Assert.Equal(HalyardRpcsChannel.HeaderLength + 16, frameLength);
    }

    [Fact]
    public void Open_RejectsAFrameWhoseLengthDisagreesWithTheHeader()
    {
        var channel = new HalyardRpcsChannel(new FakeControlCrypto());
        byte[] frame = channel.Seal("x"u8);
        Assert.Null(channel.Open(frame.AsSpan(0, frame.Length - 1).ToArray())); // one byte short of the declared body
    }

    /// <summary>
    /// A deterministic AEAD double matching <see cref="IHalyardV2ControlCrypto"/>'s shape: ciphertext equals the
    /// plaintext (same length), the 16-byte "tag" binds the AAD + counter, and open reverses seal. It does not
    /// distinguish the two directions — it stands in for the GCM envelope so these tests exercise the framing, not
    /// AES-GCM.
    /// </summary>
    private sealed class FakeControlCrypto : IHalyardV2ControlCrypto
    {
        public bool IsControlEstablished => true;
        public byte[] GenerateControlPublicKey() => throw new NotSupportedException();
        public byte[] ComputeRpHmac(ReadOnlySpan<byte> k16, ReadOnlySpan<byte> publicKey) => throw new NotSupportedException();
        public bool EstablishControl(ReadOnlySpan<byte> consolePublicKey, ReadOnlySpan<byte> k16) => true;

        public byte[] SealClientToConsole(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, ulong counter)
            => [.. plaintext, .. Tag(aad, plaintext, counter)];

        public byte[]? OpenConsoleToClient(ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad, ulong counter)
        {
            if (ciphertextAndTag.Length < 16)
                return null;
            ReadOnlySpan<byte> ct = ciphertextAndTag[..^16];
            ReadOnlySpan<byte> tag = ciphertextAndTag[^16..];
            if (!CryptographicOperations.FixedTimeEquals(tag, Tag(aad, ct, counter)))
                return null;
            return ct.ToArray();
        }

        private static byte[] Tag(ReadOnlySpan<byte> aad, ReadOnlySpan<byte> data, ulong counter)
        {
            Span<byte> ctr = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(ctr, counter);
            byte[] input = [.. aad, .. data, .. ctr];
            return SHA256.HashData(input)[..16];
        }
    }
}
