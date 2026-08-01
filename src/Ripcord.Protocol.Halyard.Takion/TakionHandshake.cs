using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Takion;

/// <summary>
/// The Takion connection handshake — SCTP's 4-way exchange carried in control packets (spec §8,
/// wire-validated). The client active-opens:
/// <code>
/// client -> server : INIT        (chunk 0x01)  { initiate_tag, a_rwnd, out_streams, in_streams, initial_tsn }
/// server -> client : INIT_ACK    (chunk 0x02)  { server_tag, a_rwnd, streams, tsn, 32-byte cookie }
/// client -> server : COOKIE_ECHO (chunk 0x0a)  echoes the 32-byte cookie
/// server -> client : COOKIE_ACK  (chunk 0x0b)  -> ESTABLISHED
/// </code>
/// Each side's tag is the verification tag the peer echoes in every later packet's header; it also seeds
/// that direction's initial sequence number. This type builds the client's outgoing packets and parses the
/// server's INIT_ACK; it holds no socket (the caller owns I/O).
/// </summary>
public sealed class TakionHandshake
{
    // Observed on the wire; the advertised receive window and stream counts.
    public const uint DefaultReceiveWindow = 0x00019000;
    public const ushort DefaultOutStreams = 0x0064;
    public const ushort DefaultInStreams = 0x0064;
    public const int CookieLength = 32;

    private const int InitChunkLength = 4 + 16;               // header + 16-byte value
    private const int InitPacketLength = TakionMessageHeader.Length + InitChunkLength;

    public enum HandshakeState { Idle, InitSent, Established }

    public HandshakeState State { get; private set; } = HandshakeState.Idle;

    /// <summary>Our verification tag (also our initial TSN); the server must echo it.</summary>
    public uint LocalTag { get; private set; }

    /// <summary>The server's verification tag, learned from INIT_ACK; we echo it in every later packet.</summary>
    public uint RemoteTag { get; private set; }

    /// <summary>Build the INIT packet and move to <see cref="HandshakeState.InitSent"/>.</summary>
    public byte[] BuildInit(uint localTag)
    {
        LocalTag = localTag;
        var packet = new byte[InitPacketLength];
        var span = packet.AsSpan();

        // Header: verification tag is 0 - the server tag is not yet known.
        new TakionMessageHeader(TakionMessageHeader.BaseTypeControl, verificationTag: 0).Write(span);

        var chunk = span[TakionMessageHeader.Length..];
        chunk[0] = (byte)SctpChunkType.Init;
        chunk[1] = 0x00; // flags
        BinaryPrimitives.WriteUInt16BigEndian(chunk[2..], (ushort)InitChunkLength);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[4..], localTag);              // initiate_tag
        BinaryPrimitives.WriteUInt32BigEndian(chunk[8..], DefaultReceiveWindow);  // a_rwnd
        BinaryPrimitives.WriteUInt16BigEndian(chunk[12..], DefaultOutStreams);
        BinaryPrimitives.WriteUInt16BigEndian(chunk[14..], DefaultInStreams);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[16..], localTag);             // initial_tsn == tag

        State = HandshakeState.InitSent;
        return packet;
    }

    /// <summary>
    /// Parse a server INIT_ACK, capturing the server's verification tag and the 32-byte cookie. Returns
    /// false if the packet is not a well-formed INIT_ACK for our connection.
    /// </summary>
    public bool TryHandleInitAck(ReadOnlySpan<byte> packet, out byte[] cookie)
    {
        cookie = [];
        if (State != HandshakeState.InitSent ||
            !TakionMessageHeader.TryParse(packet, out var header) ||
            header.BaseType != TakionMessageHeader.BaseTypeControl)
        {
            return false;
        }

        // The server echoes our tag in the header verification-tag field.
        if (header.VerificationTag != LocalTag)
        {
            return false;
        }

        var chunk = packet[TakionMessageHeader.Length..];
        if (chunk.Length < 4 || chunk[0] != (byte)SctpChunkType.InitAck)
        {
            return false;
        }

        int chunkLength = BinaryPrimitives.ReadUInt16BigEndian(chunk[2..]);
        if (chunkLength < 4 + 16 + CookieLength || chunk.Length < chunkLength)
        {
            return false;
        }

        RemoteTag = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]); // server initiate_tag
        cookie = chunk.Slice(4 + 16, CookieLength).ToArray();
        return true;
    }

    /// <summary>Build the COOKIE_ECHO packet that echoes the server's cookie (uses the server's tag).</summary>
    public byte[] BuildCookieEcho(ReadOnlySpan<byte> cookie)
    {
        if (cookie.Length != CookieLength)
            throw new ArgumentException($"Cookie must be {CookieLength} bytes.", nameof(cookie));

        const int chunkLength = 4 + CookieLength;
        var packet = new byte[TakionMessageHeader.Length + chunkLength];
        var span = packet.AsSpan();

        new TakionMessageHeader(TakionMessageHeader.BaseTypeControl, verificationTag: RemoteTag).Write(span);

        var chunk = span[TakionMessageHeader.Length..];
        chunk[0] = (byte)SctpChunkType.CookieEcho;
        chunk[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(chunk[2..], (ushort)chunkLength);
        cookie.CopyTo(chunk[4..]);
        return packet;
    }

    /// <summary>
    /// Note receipt of the server's COOKIE_ACK (or any established-connection traffic) and transition to
    /// <see cref="HandshakeState.Established"/>. Returns true when the connection becomes established.
    /// </summary>
    public bool TryComplete(ReadOnlySpan<byte> packet)
    {
        if (State != HandshakeState.InitSent && State != HandshakeState.Established)
        {
            return false;
        }

        if (!TakionMessageHeader.TryParse(packet, out var header) || header.VerificationTag != LocalTag)
        {
            return false;
        }

        var chunk = packet[TakionMessageHeader.Length..];
        // Explicit COOKIE_ACK, or the server already sending DATA on our connection, both mean established.
        if (chunk.Length >= 1 && (chunk[0] == (byte)SctpChunkType.CookieAck || chunk[0] == (byte)SctpChunkType.Data))
        {
            State = HandshakeState.Established;
            return true;
        }

        return false;
    }
}
