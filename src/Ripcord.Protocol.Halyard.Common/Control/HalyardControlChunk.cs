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
/// <param name="Prefix">
/// The 2-bit field in the top of the length word. <b>Always <c>0b11</c> in every capture we hold</b>, which is
/// why the framing was first read as "the top two bits are set" — but the vendor's own parser splits that byte
/// as <c>value &gt;&gt; 6</c> and <c>value &amp; 0x3F</c>, i.e. a 2-bit field beside a 6-bit one. So a chunk
/// carrying a different value here is well-formed, and rejecting it (as this codec did) would silently drop
/// traffic the peer considers valid. Its meaning is <b>[X]</b> — a version or class, on the evidence available.
/// </param>
/// <param name="Body">
/// Everything after the 8-byte prefix. Deliberately not decomposed further: the layout past the prefix is
/// type-specific (some chunks carry a sequence, some a sequence and an acknowledgement, the console's cookie
/// and close carry neither), and pretending otherwise would put a union in the codec. The channel that speaks
/// the handshake is where that knowledge belongs.
/// </param>
public readonly record struct HalyardControlChunk(
    HalyardControlChunkType Type,
    byte Flags,
    ReadOnlyMemory<byte> Body,
    byte Prefix = HalyardControlChunkCodec.ObservedPrefix);

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
/// <b>Framing.</b> A 2-byte big-endian header carrying a 2-bit field and, in its low 14 bits, the chunk's total
/// length <em>including</em> those two bytes; then two <c>0x244F</c> words — which are the control port 9295,
/// not a magic — then a type and a flags byte. Several chunks may be concatenated in one datagram with no padding, which is not a theoretical
/// case: the registration POST arrives as a 12-byte acknowledgement followed immediately by a 744-byte data
/// chunk in one datagram.
/// </para>
///
/// <para>
/// <b>The <c>0x244F</c> pair is the control port, 9295.</b> Read from the vendor library, where every
/// occurrence of that value as an immediate is a TCP connect in the registration path. So these are (source,
/// destination) service ports and the 9303 datagram layer is a tunnel carrying a logical 9295 control stream —
/// which is why the pair is identical across sessions and directions, and why <c>rgst</c>/<c>init</c>/
/// <c>ctrl</c> are byte-identical on both transports. Earlier readings had it as a per-session connection id
/// and then as a fixed magic; both are superseded. A chunk not carrying it is still rejected, because every
/// stream we speak uses that port.
/// </para>
/// </summary>
public static class HalyardControlChunkCodec
{
    /// <summary>Length, the two magic words, type and flags — the part every chunk shares.</summary>
    public const int PrefixLength = 8;

    /// <summary>
    /// The constant that occupies offsets 2 and 4. A required on-wire value, not a naming choice.
    /// </summary>
    public const ushort Magic = 0x244F;

    /// <summary>The length field is 14 bits, so a chunk cannot exceed this — including its own prefix.</summary>
    public const int MaxChunkLength = 0x3FFF;

    /// <summary>
    /// The value every observed chunk carries in the 2-bit field above the length. Written on the way out
    /// because it is what the console has been seen to accept; <b>not</b> required on the way in, because the
    /// vendor's parser treats those bits as a field rather than a marker.
    /// </summary>
    public const byte ObservedPrefix = 0b11;

    /// <summary>How far to shift the 2-bit prefix field within the length word.</summary>
    private const int PrefixShift = 14;

    /// <summary>The length occupies the low 14 bits of the word.</summary>
    private const ushort LengthMask = 0x3FFF;

    /// <summary>How many bytes <paramref name="bodyLength"/> needs on the wire.</summary>
    public static int ChunkLength(int bodyLength) => PrefixLength + bodyLength;

    /// <summary>
    /// Write one chunk. Returns the number of bytes written.
    /// </summary>
    public static int Write(
        Span<byte> destination, HalyardControlChunkType type, byte flags, ReadOnlySpan<byte> body)
    {
        int total = ChunkLength(body.Length);
        if (total > MaxChunkLength)
        {
            throw new ArgumentException(
                $"A chunk may be at most {MaxChunkLength} bytes including its prefix; this one would be {total}.",
                nameof(body));
        }

        if (destination.Length < total)
        {
            throw new ArgumentException(
                $"Destination is {destination.Length} bytes; this chunk needs {total}.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(
            destination, (ushort)((ObservedPrefix << PrefixShift) | (ushort)total));
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], Magic);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], Magic);
        destination[6] = (byte)type;
        destination[7] = flags;
        body.CopyTo(destination[PrefixLength..]);
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
    /// False for anything that is not a well-formed chunk: too short, the length bits clear, a length that
    /// undercuts the prefix or overruns the datagram, or either magic word wrong. A malformed datagram is
    /// dropped rather than partially interpreted — this is a transport that anyone on the network can send to.
    /// </returns>
    public static bool TryRead(ReadOnlySpan<byte> datagram, out HalyardControlChunk chunk, out int consumed)
    {
        chunk = default;
        consumed = 0;

        if (datagram.Length < PrefixLength)
        {
            return false;
        }

        // The prefix is READ, not required: the vendor's parser splits this byte into a 2-bit and a 6-bit
        // field, so a value other than the observed 0b11 is well-formed and dropping it would lose traffic
        // the peer thinks it sent us.
        ushort header = BinaryPrimitives.ReadUInt16BigEndian(datagram);
        byte prefix = (byte)(header >> PrefixShift);
        int total = header & LengthMask;
        if (total < PrefixLength || total > datagram.Length)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]) != Magic
            || BinaryPrimitives.ReadUInt16BigEndian(datagram[4..]) != Magic)
        {
            return false;
        }

        chunk = new HalyardControlChunk(
            (HalyardControlChunkType)datagram[6],
            datagram[7],
            datagram[PrefixLength..total].ToArray(),
            prefix);
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
    /// malformed yields nothing.
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
