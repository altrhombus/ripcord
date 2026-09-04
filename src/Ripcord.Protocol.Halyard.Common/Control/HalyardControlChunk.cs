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
/// <param name="Body">
/// Everything after the 8-byte prefix. Deliberately not decomposed further: the layout past the prefix is
/// type-specific (some chunks carry a sequence, some a sequence and an acknowledgement, the console's cookie
/// and close carry neither), and pretending otherwise would put a union in the codec. The channel that speaks
/// the handshake is where that knowledge belongs.
/// </param>
public readonly record struct HalyardControlChunk(
    HalyardControlChunkType Type,
    byte Flags,
    ReadOnlyMemory<byte> Body);

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
/// <b>Framing.</b> A 2-byte big-endian header whose top two bits are set and whose low 14 bits are the chunk's
/// total length <em>including</em> those two bytes, then two constant <c>0x244F</c> words, then a type and a
/// flags byte. Several chunks may be concatenated in one datagram with no padding, which is not a theoretical
/// case: the registration POST arrives as a 12-byte acknowledgement followed immediately by a 744-byte data
/// chunk in one datagram.
/// </para>
///
/// <para>
/// The <c>0x244F</c> pair was once read as a per-session connection id. It is not: the identical value appears
/// at that offset across four independently established sessions on different networks, which a random
/// per-session value would not do. It is treated as a constant, and a chunk that does not carry it is rejected
/// rather than guessed at.
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

    /// <summary>The two high bits of the length word, always set.</summary>
    private const ushort LengthFlagBits = 0xC000;

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

        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(LengthFlagBits | (ushort)total));
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

        ushort header = BinaryPrimitives.ReadUInt16BigEndian(datagram);
        if ((header & LengthFlagBits) != LengthFlagBits)
        {
            return false;
        }

        int total = header & ~LengthFlagBits;
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
            datagram[PrefixLength..total].ToArray());
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
