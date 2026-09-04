using Ripcord.Protocol.Halyard.Common.Control;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The account-route control transport's framing: the 88-byte prelude and the chunk layer
/// (<c>docs/protocol/ps5-session-transport.md</c>).
///
/// <para>
/// Synthetic cases only, here. The bytes of a real exchange carry per-session material and the encrypted
/// pairing record, so those live in the gitignored dirty room and are exercised by
/// <see cref="LiveAccountTransportVectorTests"/> instead — the same split every other captured-ground-truth
/// suite in this project uses.
/// </para>
/// </summary>
public class HalyardControlChunkTests
{
    // ---- chunk layer ---------------------------------------------------------------------------

    [Fact]
    public void Chunk_RoundTrips()
    {
        byte[] body = [1, 2, 3, 4, 5];

        byte[] wire = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, body);

        Assert.True(HalyardControlChunkCodec.TryRead(wire, out HalyardControlChunk chunk, out int consumed));
        Assert.Equal(HalyardControlChunkType.Data, chunk.Type);
        Assert.Equal(0x30, chunk.Flags);
        Assert.Equal(body, chunk.Body.ToArray());
        Assert.Equal(wire.Length, consumed);
    }

    [Fact]
    public void Chunk_PrefixBitsAreAFieldToBeRead_NotAMarkerToBeRequired()
    {
        // This codec used to reject any chunk whose top two bits were not both set, on the reading that they
        // were a fixed marker. The vendor's own parser splits that byte as (value >> 6) and (value & 0x3F) —
        // a 2-bit field beside the length's high 6 bits — so a different value is well-formed and refusing it
        // would silently drop traffic the peer believes it sent. Every capture we hold carries 0b11.
        byte[] wire = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, [1, 2]);
        wire[0] = (byte)(wire[0] & 0x3F);   // prefix 0b00, length untouched

        Assert.True(HalyardControlChunkCodec.TryRead(wire, out HalyardControlChunk chunk, out int consumed));
        Assert.Equal(0, chunk.Prefix);
        Assert.Equal(wire.Length, consumed);
        Assert.Equal(new byte[] { 1, 2 }, chunk.Body.ToArray());
    }

    [Fact]
    public void Chunk_WritesTheObservedPrefix()
    {
        byte[] wire = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, [1, 2]);

        Assert.True(HalyardControlChunkCodec.TryRead(wire, out HalyardControlChunk chunk, out _));
        Assert.Equal(HalyardControlChunkCodec.ObservedPrefix, chunk.Prefix);
    }

    [Fact]
    public void Chunk_LengthFieldCountsItsOwnPrefix()
    {
        // The property that makes concatenation work: the length is the whole chunk, not the payload. Reading
        // it as a payload length puts every subsequent chunk in a datagram 8 bytes out of place.
        byte[] wire = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Ack, 0x30, [0xAA, 0xBB]);

        Assert.Equal(10, wire.Length);
        Assert.Equal(0xC0, wire[0]);
        Assert.Equal(0x0A, wire[1]);
    }

    [Fact]
    public void Chunk_CarriesTheMagicAtBothOffsets()
    {
        byte[] wire = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Hello, 0x30, []);

        Assert.Equal(new byte[] { 0x24, 0x4F, 0x24, 0x4F }, wire[2..6]);
    }

    [Fact]
    public void Chunk_LongBody_SetsOnlyTheTopTwoBitsOfTheLength()
    {
        // A 286-byte datagram begins C1 1E on the wire: 0x11E is the length and the C1 is not a second message
        // type, which is what the earlier "0xC0/0xC1 header" reading took it for.
        byte[] wire = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, new byte[278]);

        Assert.Equal(286, wire.Length);
        Assert.Equal(0xC1, wire[0]);
        Assert.Equal(0x1E, wire[1]);
    }

    [Fact]
    public void ReadAll_SplitsConcatenatedChunks()
    {
        // Exactly the shape the registration POST arrives in: an acknowledgement then a data chunk, one
        // datagram.
        byte[] ack = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Ack, 0x30, [0x2B, 0xFA, 0xA6, 0x5F]);
        byte[] data = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, "POST /"u8.ToArray());
        byte[] datagram = [.. ack, .. data];

        List<HalyardControlChunk> chunks = HalyardControlChunkCodec.ReadAll(datagram);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(HalyardControlChunkType.Ack, chunks[0].Type);
        Assert.Equal(HalyardControlChunkType.Data, chunks[1].Type);
        Assert.Equal("POST /"u8.ToArray(), chunks[1].Body.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0xC0, 0x08, 0x00, 0x00, 0x24, 0x4F, 0x02, 0x30 })]   // first magic wrong
    [InlineData(new byte[] { 0xC0, 0x08, 0x24, 0x4F, 0x00, 0x00, 0x02, 0x30 })]   // second magic wrong
    [InlineData(new byte[] { 0xC0, 0x04, 0x24, 0x4F, 0x24, 0x4F, 0x02, 0x30 })]   // length undercuts the prefix
    [InlineData(new byte[] { 0xC0, 0xFF, 0x24, 0x4F, 0x24, 0x4F, 0x02, 0x30 })]   // length overruns the datagram
    [InlineData(new byte[] { 0xC0, 0x08, 0x24, 0x4F })]                            // truncated
    public void Chunk_Malformed_IsRejectedRatherThanGuessedAt(byte[] datagram)
    {
        // Anyone on the network can send to this port, so a reader that half-interpreted a bad datagram would
        // be taking dictation from strangers.
        Assert.False(HalyardControlChunkCodec.TryRead(datagram, out _, out _));
        Assert.Empty(HalyardControlChunkCodec.ReadAll(datagram));
    }

    [Fact]
    public void ReadAll_KeepsTheChunksItAlreadyParsed_WhenTrailingBytesAreRubbish()
    {
        byte[] good = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, [9, 9]);
        byte[] datagram = [.. good, 0x00, 0x01, 0x02];

        List<HalyardControlChunk> chunks = HalyardControlChunkCodec.ReadAll(datagram);

        Assert.Single(chunks);
        Assert.Equal(new byte[] { 9, 9 }, chunks[0].Body.ToArray());
    }

    [Fact]
    public void Chunk_TooLongForTheLengthField_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, new byte[0x3FFF]));
    }

    // ---- prelude ------------------------------------------------------------------------------

    [Fact]
    public void Prelude_RoundTrips()
    {
        byte[] mine = [.. Enumerable.Range(1, 20).Select(i => (byte)i)];
        byte[] theirs = [.. Enumerable.Range(100, 20).Select(i => (byte)i)];
        var prelude = new HalyardControlPrelude(
            HalyardControlPrelude.Init, mine, theirs,
            TagPair: 0x000168B9, RequestWord: 0x19, Token: 0, Tail: new byte[8]);

        byte[] wire = prelude.Serialize();

        Assert.Equal(HalyardControlPrelude.Length, wire.Length);
        Assert.True(HalyardControlPrelude.TryParse(wire, out HalyardControlPrelude parsed));
        Assert.Equal(prelude.Type, parsed.Type);
        Assert.Equal(mine, parsed.SenderId.ToArray());
        Assert.Equal(theirs, parsed.PeerId.ToArray());
        Assert.Equal(prelude.TagPair, parsed.TagPair);
        Assert.Equal(prelude.RequestWord, parsed.RequestWord);
    }

    [Fact]
    public void Prelude_TypeIsLittleEndian()
    {
        // The one little-endian field in the protocol, so it is worth a test of its own: written big-endian it
        // becomes 0x06000000 and the console sees an unknown type.
        byte[] wire = new HalyardControlPrelude(
            HalyardControlPrelude.Init, new byte[20], new byte[20], 0, 0, 0, ReadOnlyMemory<byte>.Empty).Serialize();

        Assert.Equal(new byte[] { 0x06, 0x00, 0x00, 0x00 }, wire[..4]);
    }

    [Fact]
    public void Prelude_SwappedTagPair_ExchangesTheHalves()
    {
        // The console answers with the halves exchanged, which is what identifies them as a pair rather than
        // one 32-bit value.
        var prelude = new HalyardControlPrelude(
            HalyardControlPrelude.Init, new byte[20], new byte[20],
            TagPair: 0x000168B9, RequestWord: 0, Token: 0, Tail: ReadOnlyMemory<byte>.Empty);

        Assert.Equal(0x68B90001u, prelude.SwappedTagPair);
    }

    [Theory]
    [InlineData(87)]
    [InlineData(89)]
    public void Prelude_WrongLength_IsRejected(int length)
    {
        byte[] datagram = new byte[length];
        datagram[0] = 6;

        Assert.False(HalyardControlPrelude.TryParse(datagram, out _));
    }

    [Fact]
    public void Prelude_UnknownType_IsRejected()
    {
        // The prelude and the chunk layer share a port, so the reader must be able to tell them apart.
        byte[] datagram = new byte[HalyardControlPrelude.Length];
        datagram[0] = 5;

        Assert.False(HalyardControlPrelude.TryParse(datagram, out _));
    }

    [Fact]
    public void Prelude_AndChunk_AreNotConfusableForEachOther()
    {
        byte[] chunk = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, new byte[80]);
        Assert.Equal(HalyardControlPrelude.Length, chunk.Length);   // same size, deliberately

        Assert.False(HalyardControlPrelude.TryParse(chunk, out _));

        byte[] prelude = new HalyardControlPrelude(
            HalyardControlPrelude.Init, new byte[20], new byte[20], 0, 0, 0, ReadOnlyMemory<byte>.Empty).Serialize();
        Assert.False(HalyardControlChunkCodec.TryRead(prelude, out _, out _));
    }

    [Fact]
    public void Prelude_WrongHashedIdLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new HalyardControlPrelude(
            HalyardControlPrelude.Init, new byte[19], new byte[20], 0, 0, 0, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => new HalyardControlPrelude(
            HalyardControlPrelude.Init, new byte[20], new byte[21], 0, 0, 0, ReadOnlyMemory<byte>.Empty));
    }
}
