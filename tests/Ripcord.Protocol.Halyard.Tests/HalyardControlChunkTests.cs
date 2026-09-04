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
    public void Chunk_WritesThePairedWordCount()
    {
        byte[] wire = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, [1, 2]);

        Assert.True(HalyardControlChunkCodec.TryRead(wire, out HalyardControlChunk chunk, out _));
        Assert.Equal(HalyardControlChunkCodec.PairedWordCount, chunk.WordCount);
        Assert.Equal(HalyardControlChunkCodec.ControlPort, chunk.SourcePort);
        Assert.Equal(HalyardControlChunkCodec.ControlPort, chunk.DestinationPort);
    }

    [Fact]
    public void Chunk_WordCountOfTwo_CarriesOnePortUsedForBothEnds()
    {
        // The top two bits are a count of 16-bit words in the prefix, this one included — not a marker, and
        // not part of the length. A count of 2 therefore means one port word follows, and the receiver
        // requires it to match both the local and the peer port of the connection it selects. We have never
        // had to send this shape, but the peer may, and reading it as the paired shape would put the type and
        // flags two bytes out of place.
        byte[] wire = [0x80, 0x06, 0x24, 0x4F, 0x02, 0x30];

        Assert.True(HalyardControlChunkCodec.TryRead(wire, out HalyardControlChunk chunk, out int consumed));
        Assert.Equal(2, chunk.WordCount);
        Assert.Equal(HalyardControlChunkType.Data, chunk.Type);
        Assert.Equal(0x30, chunk.Flags);
        Assert.Equal(HalyardControlChunkCodec.ControlPort, chunk.DestinationPort);
        Assert.Equal(chunk.DestinationPort, chunk.SourcePort);
        Assert.Equal(6, consumed);
    }

    [Fact]
    public void Chunk_WordCountOfOne_CarriesNoPortAtAll()
    {
        // A count of 1 is the header alone; the receiver then matches on the peer address rather than a port.
        byte[] wire = [0x40, 0x05, 0x02, 0x30, 0x77];

        Assert.True(HalyardControlChunkCodec.TryRead(wire, out HalyardControlChunk chunk, out int consumed));
        Assert.Equal(1, chunk.WordCount);
        Assert.Equal(HalyardControlChunkType.Data, chunk.Type);
        Assert.Equal(new byte[] { 0x77 }, chunk.Body.ToArray());
        Assert.Equal(5, consumed);
    }

    [Fact]
    public void Chunk_PortWordsAreReadThrough_NotRequiredToBeTheControlPort()
    {
        // This codec used to reject any chunk whose port words were not 0x244F, on the reading that they were
        // a fixed magic. They are ports: the receiver demultiplexes on the second one and does not constrain
        // it to a constant, so refusing another value would drop traffic the peer believes it sent. Every
        // capture we hold carries the control port at both.
        byte[] wire = [0xC0, 0x08, 0x11, 0x22, 0x33, 0x44, 0x02, 0x30];

        Assert.True(HalyardControlChunkCodec.TryRead(wire, out HalyardControlChunk chunk, out _));
        Assert.Equal(0x1122, chunk.SourcePort);
        Assert.Equal(0x3344, chunk.DestinationPort);
    }

    [Fact]
    public void Chunk_ReservedLengthBits_AreMaskedAwayRatherThanRead()
    {
        // The length is 11 bits, and the receiver masks bits 13..11 off. An earlier reading had the length as
        // 14 bits, which would read a chunk carrying anything in those bits as thousands of bytes long and
        // abandon the datagram.
        byte[] wire = HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, [1, 2]);
        wire[0] |= 0x38;   // set every reserved bit; count and length untouched

        Assert.True(HalyardControlChunkCodec.TryRead(wire, out HalyardControlChunk chunk, out int consumed));
        Assert.Equal(wire.Length, consumed);
        Assert.Equal(new byte[] { 1, 2 }, chunk.Body.ToArray());
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
    public void Chunk_CarriesTheControlPortAtBothWords()
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
    [InlineData(new byte[] { 0x00, 0x08, 0x24, 0x4F, 0x24, 0x4F, 0x02, 0x30 })]   // word count of zero
    [InlineData(new byte[] { 0xC0, 0x04, 0x24, 0x4F, 0x24, 0x4F, 0x02, 0x30 })]   // length undercuts the prefix
    [InlineData(new byte[] { 0xC0, 0x07, 0x24, 0x4F, 0x24, 0x4F, 0x02, 0x30 })]   // no room for type and flags
    [InlineData(new byte[] { 0xC0, 0xFF, 0x24, 0x4F, 0x24, 0x4F, 0x02, 0x30 })]   // length overruns the datagram
    [InlineData(new byte[] { 0xC0, 0x08, 0x24, 0x4F })]                            // truncated
    [InlineData(new byte[] { 0xC0 })]                                              // shorter than the header
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
        // 11 bits, so 2047 total. A caller with more to say has to fragment, and finding that out from an
        // exception here beats the console silently abandoning the datagram.
        int longestBody = HalyardControlChunkCodec.MaxChunkLength
            - HalyardControlChunkCodec.PairedPrefixLength
            - HalyardControlChunkCodec.TypeAndFlagsLength;

        byte[] wire = HalyardControlChunkCodec.Encode(
            HalyardControlChunkType.Data, 0x30, new byte[longestBody]);
        Assert.Equal(HalyardControlChunkCodec.MaxChunkLength, wire.Length);
        Assert.True(HalyardControlChunkCodec.TryRead(wire, out _, out int consumed));
        Assert.Equal(wire.Length, consumed);

        Assert.Throws<ArgumentException>(() =>
            HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, new byte[longestBody + 1]));
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
