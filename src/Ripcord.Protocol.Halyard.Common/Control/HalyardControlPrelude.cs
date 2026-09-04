using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Common.Control;

/// <summary>
/// The 88-byte prelude that opens the account-route control transport, before any chunk traffic.
///
/// <para>
/// <b>What it is for.</b> Two datagram types — an <see cref="Init"/> both sides send and a
/// <see cref="CookieEcho"/> the client returns — carrying <b>both peers' 20-byte <c>localHashedId</c>
/// values</b>, the same ones each side published in its signaling OFFER. That is the join between the cloud
/// rendezvous and the direct transport, and it is why this transport cannot be opened without having
/// completed the candidate exchange first: the console's <c>localHashedId</c> arrives only in its OFFER.
/// (An earlier reading called these "two ~20-byte high-entropy blobs" of unknown origin — they are the
/// hashed ids.)
/// </para>
///
/// <para>
/// Derived from this project's own <c>cap64</c>; see <c>docs/protocol/ps5-session-transport.md</c>. That
/// capture is a <b>WAN</b> pair, so the prelude is observed doing double duty as the NAT hole-punch. **[X]
/// Whether a same-LAN client may skip it is untested**; the faithful thing is to send it either way.
/// </para>
/// </summary>
public readonly record struct HalyardControlPrelude
{
    /// <summary>Every prelude datagram is exactly this long, in both directions and both types.</summary>
    public const int Length = 88;

    /// <summary>A <c>localHashedId</c> is 20 bytes — the same size the signaling OFFER carries.</summary>
    public const int HashedIdLength = 20;

    /// <summary>Opens the exchange; sent by both sides.</summary>
    public const uint Init = 6;

    /// <summary>The client's reply once it holds the console's cookie.</summary>
    public const uint CookieEcho = 7;

    private const int SenderIdOffset = 4;
    private const int PeerIdOffset = 36;
    private const int TagPairOffset = 68;
    private const int RequestWordOffset = 72;
    private const int TokenOffset = 76;
    private const int TailOffset = 80;

    /// <param name="Type"><see cref="Init"/> or <see cref="CookieEcho"/>. Little-endian on the wire — the only
    /// little-endian field in this protocol, which is itself a hint that the prelude predates the rest.</param>
    /// <param name="SenderId">The sender's own <c>localHashedId</c>, 20 bytes.</param>
    /// <param name="PeerId">The peer's <c>localHashedId</c>, 20 bytes, known from its OFFER.</param>
    /// <param name="TagPair">
    /// Two big-endian 16-bit values. The client sends <c>(1, X)</c> and the console answers with the pair
    /// <b>swapped</b>, which is what identifies them as a pair rather than one 32-bit value. **[X]** their
    /// meaning past that is unconfirmed; they read as session-id halves.
    /// </param>
    /// <param name="RequestWord">
    /// Present in the client's <see cref="Init"/> (observed <c>0x00000019</c>) and zero in the console's reply
    /// and in the <see cref="CookieEcho"/>. **[X] unexplained.**
    /// </param>
    /// <param name="Token">
    /// The connection token this datagram asserts. In an <see cref="Init"/> it is the <b>sender's own</b>
    /// (client and console each announce a different one); in the <see cref="CookieEcho"/> it is the
    /// <b>peer's</b>, returned verbatim. So the exchange is a mutual verification-tag handshake, not a one-way
    /// cookie — the console commits no state until it sees its own token come back, which is the same reason
    /// Takion's handshake echoes a cookie.
    ///
    /// <para>
    /// An earlier draft of this type had it as "zero in the client's Init, set by the console" — wrong, and
    /// caught by <c>LiveAccountTransportVectorTests</c> against the captured bytes rather than by reading.
    /// </para>
    /// </param>
    /// <param name="Tail">
    /// Eight trailing bytes, zero except in the client's <see cref="CookieEcho"/>, where six are populated.
    /// **[X] unexplained.**
    /// </param>
    public HalyardControlPrelude(
        uint Type,
        ReadOnlyMemory<byte> SenderId,
        ReadOnlyMemory<byte> PeerId,
        uint TagPair,
        uint RequestWord,
        uint Token,
        ReadOnlyMemory<byte> Tail)
    {
        if (SenderId.Length != HashedIdLength)
        {
            throw new ArgumentException($"SenderId must be {HashedIdLength} bytes.", nameof(SenderId));
        }

        if (PeerId.Length != HashedIdLength)
        {
            throw new ArgumentException($"PeerId must be {HashedIdLength} bytes.", nameof(PeerId));
        }

        if (Tail.Length > 8)
        {
            throw new ArgumentException("Tail is at most 8 bytes.", nameof(Tail));
        }

        this.Type = Type;
        this.SenderId = SenderId;
        this.PeerId = PeerId;
        this.TagPair = TagPair;
        this.RequestWord = RequestWord;
        this.Token = Token;
        this.Tail = Tail;
    }

    public uint Type { get; }

    public ReadOnlyMemory<byte> SenderId { get; }

    public ReadOnlyMemory<byte> PeerId { get; }

    public uint TagPair { get; }

    public uint RequestWord { get; }

    public uint Token { get; }

    public ReadOnlyMemory<byte> Tail { get; }

    /// <summary>The <see cref="TagPair"/> with its two halves exchanged — the form the peer answers with.</summary>
    public uint SwappedTagPair => (TagPair >> 16) | (TagPair << 16);

    /// <summary>Serialize to the 88 bytes that go on the wire. Every unlisted byte is zero.</summary>
    public byte[] Serialize()
    {
        byte[] datagram = new byte[Length];
        BinaryPrimitives.WriteUInt32LittleEndian(datagram, Type);
        SenderId.Span.CopyTo(datagram.AsSpan(SenderIdOffset));
        PeerId.Span.CopyTo(datagram.AsSpan(PeerIdOffset));
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(TagPairOffset), TagPair);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(RequestWordOffset), RequestWord);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(TokenOffset), Token);
        Tail.Span.CopyTo(datagram.AsSpan(TailOffset));
        return datagram;
    }

    /// <summary>
    /// Parse an 88-byte prelude datagram. False for anything of the wrong length or an unknown type — the
    /// chunk layer and this one share a port, so a reader has to be able to tell them apart, and a chunk
    /// always begins with its two high length bits set where this has a small little-endian type.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, out HalyardControlPrelude prelude)
    {
        prelude = default;

        if (datagram.Length != Length)
        {
            return false;
        }

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(datagram);
        if (type is not (Init or CookieEcho))
        {
            return false;
        }

        prelude = new HalyardControlPrelude(
            type,
            datagram.Slice(SenderIdOffset, HashedIdLength).ToArray(),
            datagram.Slice(PeerIdOffset, HashedIdLength).ToArray(),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[TagPairOffset..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[RequestWordOffset..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[TokenOffset..]),
            datagram[TailOffset..Length].ToArray());
        return true;
    }
}
