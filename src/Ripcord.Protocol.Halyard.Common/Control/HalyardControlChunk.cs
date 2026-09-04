using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Common.Control;

/// <summary>
/// Chunk types on the account-route control transport (UDP 9303), values wire-observed in this project's own
/// <c>cap64</c> — see <c>docs/protocol/ps5-session-transport.md</c>, "The account-route control transport".
///
/// <para>
/// Named for what they do in the exchange rather than by any vendor symbol, per the naming rules in
/// CLAUDE.md. The handshake reads as a cookie exchange: the client says hello, the console answers with a
/// cookie, the client echoes it back, and the console accepts.
/// </para>
///
/// <para>
/// <b>[X] These are observed COMBINATIONS of a flags bitmap, not an enumeration.</b> Read from the vendor
/// library, whose header codec guards with a mask (<c>(type &amp; 0x24) == 0x24</c>) and whose connection
/// engine branches on a single bit: <c>0x02</c> carries data · <c>0x20</c> carries an acknowledgement ·
/// <c>0x04</c> carries the extra 16-bit field (so <c>0x24</c> means ack <em>and</em> extra, which is exactly
/// why only that combination has a trailing field) · <c>0x80</c> is connection control · <c>0x10</c> carries
/// a cookie. Hence hello = <c>0x80</c>, hello-with-cookie = <c>0x90</c>, cookie offer = <c>0xD0</c>.
/// </para>
///
/// <para>
/// Every combination this protocol has been observed to use is named below, so nothing on the wire is
/// misread today — but a combination we have not seen would fall through as an unknown type. Modelling the
/// bits properly is the correct fix; it is deliberately not done here yet, because the flag meanings above
/// come from one mask test and one branch rather than from a full reading of the codec.
/// </para>
/// </summary>
public enum HalyardControlChunkType : byte
{
    /// <summary>Carries a payload: HTTP/1.1 text, or a binary control frame once <c>ctrl</c> has completed.</summary>
    Data = 0x02,

    /// <summary>A bare sequence acknowledgement, usually prepended to a data chunk in the same datagram.</summary>
    Ack = 0x20,

    /// <summary>
    /// An acknowledgement with two trailing bytes whose meaning is <b>[X] unexplained</b> (observed 0x21–0xD2;
    /// neither a checksum nor a window over anything we could correlate). Only appears after <c>ctrl</c>, so
    /// registration never has to emit one.
    /// </summary>
    AckExtended = 0x24,

    /// <summary>Client → console: opens the connection, offering a random connection tag.</summary>
    Hello = 0x80,

    /// <summary>Client → console: <see cref="Hello"/> again with the console's cookie echoed back.</summary>
    HelloEcho = 0x90,

    /// <summary>Console → client: the connection is open; carries the console's own initial sequence.</summary>
    Accept = 0xA0,

    /// <summary>Console → client: teardown, after the response has been delivered.</summary>
    Close = 0xC0,

    /// <summary>Console → client: the cookie the client must echo.</summary>
    Cookie = 0xD0,
}

/// <summary>
/// One chunk as it sits on the wire, with the fields that are uniform across every type.
/// </summary>
/// <param name="WordCount">
/// The 2-bit count in the top of the header word, <b>including the header word itself</b> — so it is 1, 2 or
/// 3, and <c>WordCount - 1</c> port words follow the header. Zero is malformed. Every chunk this protocol has
/// been observed to use carries 3.
/// </param>
/// <param name="SourcePort">
/// The first port word, present when <see cref="WordCount"/> is 3. The receiver does not key its lookup on
/// this one, so which of the pair is "source" is <b>[X]</b>: both are 9295 in every capture we hold, and only
/// the second is matched against a connection.
/// </param>
/// <param name="DestinationPort">
/// The word the receiver demultiplexes on — the last one present. For <see cref="WordCount"/> 3 it is the
/// second port word; for 2, the single word, which the receiver requires to equal <em>both</em> the local and
/// the peer port of the connection it matches. For 1 there is no port and the receiver matches on the peer
/// address alone; this field is then <see cref="HalyardControlChunkCodec.ControlPort"/> by default and carries
/// no wire meaning.
/// </param>
/// <param name="Body">
/// Everything after the header, its port words, and the type and flags bytes. Deliberately not decomposed
/// further: the layout past that point is type-specific (some chunks carry a sequence, some a sequence and an
/// acknowledgement, the console's cookie and close carry neither), and pretending otherwise would put a union
/// in the codec. The channel that speaks the handshake is where that knowledge belongs.
/// </param>
public readonly record struct HalyardControlChunk(
    HalyardControlChunkType Type,
    byte Flags,
    ReadOnlyMemory<byte> Body,
    ushort SourcePort = HalyardControlChunkCodec.ControlPort,
    ushort DestinationPort = HalyardControlChunkCodec.ControlPort,
    byte WordCount = HalyardControlChunkCodec.PairedWordCount);

/// <summary>
/// The chunk framing of the account ("web"/no-PIN) route's control transport.
///
/// <para>
/// <b>Not Takion.</b> That transport (A/V, and the PIN route's session) is a 13-byte header with a
/// verification tag and a GMAC tag over SCTP chunks. This is a lighter, separate framing that the account
/// route uses for the whole control plane — <c>rgst</c>, <c>init</c> and <c>ctrl</c> alike — and the two share
/// no bytes. Both exist because the account route reaches the console through the cloud rendezvous rather than
/// over a directly-opened TCP connection.
/// </para>
///
/// <para>
/// <b>Framing</b>, read from the vendor library's own receive loop rather than inferred from captures:
/// </para>
/// <code>
/// u16 big-endian: bits 15..14 = word count (1-3, counting this word)
///                 bits 13..11 = reserved, masked away by the receiver
///                 bits 10..0  = total chunk length, including this word
/// (count - 1) x u16 big-endian port words
/// u8 type, u8 flags, payload...
/// </code>
/// <para>
/// So the two <c>0x244F</c> words that every chunk of this protocol carries are not fixed structure — they are
/// what a word count of 3 means. A count of 2 carries one port word and a count of 1 none, and both shapes are
/// well-formed traffic this codec must not choke on even though we have never had to send them.
/// </para>
///
/// <para>
/// <b>The port words are the control port, 9295.</b> Read from the vendor library, where every occurrence of
/// that value as an immediate is a TCP connect in the registration path. So these are service ports and the
/// 9303 datagram layer is a tunnel carrying a logical 9295 control stream — which is why the pair is identical
/// across sessions and directions, and why <c>rgst</c>/<c>init</c>/<c>ctrl</c> are byte-identical on both
/// transports. Earlier readings had the pair as a per-session connection id and then as a fixed magic; both
/// are superseded.
/// </para>
///
/// <para>
/// Several chunks may be concatenated in one datagram with no padding, which is not a theoretical case: the
/// registration POST arrives as a 12-byte acknowledgement followed immediately by a 744-byte data chunk in one
/// datagram.
/// </para>
/// </summary>
public static class HalyardControlChunkCodec
{
    /// <summary>
    /// The service port carried in the port words. A required on-wire value, not a naming choice.
    /// </summary>
    public const ushort ControlPort = 0x244F;

    /// <summary>
    /// The word count every chunk of this protocol carries: the header plus a (source, destination) port pair.
    /// </summary>
    public const byte PairedWordCount = 3;

    /// <summary>The header word plus the two port words a <see cref="PairedWordCount"/> chunk carries.</summary>
    public const int PairedPrefixLength = PairedWordCount * 2;

    /// <summary>Type and flags, which sit after the header and its port words in every chunk.</summary>
    public const int TypeAndFlagsLength = 2;

    /// <summary>
    /// The largest total chunk length the receiver can express: the length field is <b>11 bits</b>, not the 14
    /// an earlier reading assumed. A sender must therefore fragment above this; the 744-byte registration POST
    /// fits comfortably.
    /// </summary>
    public const int MaxChunkLength = 0x7FF;

    /// <summary>How far to shift the 2-bit word count within the header word.</summary>
    private const int WordCountShift = 14;

    /// <summary>The length occupies the low 11 bits of the header word.</summary>
    private const ushort LengthMask = 0x07FF;

    /// <summary>
    /// How many bytes <paramref name="bodyLength"/> needs on the wire, as the paired shape we always send.
    /// </summary>
    public static int ChunkLength(int bodyLength) => PairedPrefixLength + TypeAndFlagsLength + bodyLength;

    /// <summary>
    /// Write one chunk in the paired shape. Returns the number of bytes written.
    /// </summary>
    public static int Write(
        Span<byte> destination, HalyardControlChunkType type, byte flags, ReadOnlySpan<byte> body)
    {
        int total = ChunkLength(body.Length);
        if (total > MaxChunkLength)
        {
            throw new ArgumentException(
                $"A chunk may be at most {MaxChunkLength} bytes including its header; this one would be "
                    + $"{total}. The receiver's length field is 11 bits, so a longer body must be fragmented.",
                nameof(body));
        }

        if (destination.Length < total)
        {
            throw new ArgumentException(
                $"Destination is {destination.Length} bytes; this chunk needs {total}.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(
            destination, (ushort)((PairedWordCount << WordCountShift) | (ushort)total));
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], ControlPort);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], ControlPort);
        destination[6] = (byte)type;
        destination[7] = flags;
        body.CopyTo(destination[(PairedPrefixLength + TypeAndFlagsLength)..]);
        return total;
    }

    /// <summary>Allocate and write one chunk — for callers that are not managing a buffer.</summary>
    public static byte[] Encode(HalyardControlChunkType type, byte flags, ReadOnlySpan<byte> body)
    {
        byte[] buffer = new byte[ChunkLength(body.Length)];
        Write(buffer, type, flags, body);
        return buffer;
    }

    /// <summary>
    /// Read the chunk at the start of <paramref name="datagram"/>.
    /// </summary>
    /// <param name="consumed">
    /// How many bytes the chunk occupied, so a caller can advance to the next one in the same datagram.
    /// </param>
    /// <returns>
    /// False for anything the vendor's own receive loop would abandon the datagram on: too short, a zero word
    /// count, a length that cannot hold the prefix it claims, or a length that overruns the datagram. A
    /// malformed datagram is dropped rather than partially interpreted — this is a transport that anyone on
    /// the network can send to.
    /// </returns>
    public static bool TryRead(ReadOnlySpan<byte> datagram, out HalyardControlChunk chunk, out int consumed)
    {
        chunk = default;
        consumed = 0;

        if (datagram.Length < 2)
        {
            return false;
        }

        ushort header = BinaryPrimitives.ReadUInt16BigEndian(datagram);
        byte words = (byte)(header >> WordCountShift);
        int total = header & LengthMask;

        // A count of zero is what makes the vendor's loop give up on the rest of the datagram. Bits 13..11 are
        // deliberately not examined: the receiver masks them away, so requiring anything of them here would
        // reject traffic it accepts.
        if (words == 0 || total > datagram.Length)
        {
            return false;
        }

        int prefix = words * 2;
        if (total < prefix + TypeAndFlagsLength)
        {
            return false;
        }

        // The words after the header are ports. The receiver demultiplexes on the last one; a count of 1
        // carries none and is matched on the peer address alone.
        ushort source = ControlPort;
        ushort destination = ControlPort;
        if (words == PairedWordCount)
        {
            source = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);
            destination = BinaryPrimitives.ReadUInt16BigEndian(datagram[4..]);
        }
        else if (words == 2)
        {
            destination = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);
            source = destination;
        }

        chunk = new HalyardControlChunk(
            (HalyardControlChunkType)datagram[prefix],
            datagram[prefix + 1],
            datagram[(prefix + TypeAndFlagsLength)..total].ToArray(),
            source,
            destination,
            words);
        consumed = total;
        return true;
    }

    /// <summary>
    /// Read every chunk in one datagram, in order.
    ///
    /// <para>
    /// Stops at the first malformed chunk and keeps what came before it, because that is the honest reading:
    /// the chunks already parsed were complete and self-describing, and a caller that discarded them because
    /// of trailing rubbish would drop a response it had in hand. A datagram whose <em>first</em> chunk is
    /// malformed yields nothing. It is also what the vendor's loop does — it breaks out and accounts for what
    /// it managed to read.
    /// </para>
    /// </summary>
    public static List<HalyardControlChunk> ReadAll(ReadOnlySpan<byte> datagram)
    {
        var chunks = new List<HalyardControlChunk>();
        int offset = 0;

        while (offset < datagram.Length)
        {
            if (!TryRead(datagram[offset..], out HalyardControlChunk chunk, out int consumed))
            {
                break;
            }

            chunks.Add(chunk);
            offset += consumed;
        }

        return chunks;
    }
}
