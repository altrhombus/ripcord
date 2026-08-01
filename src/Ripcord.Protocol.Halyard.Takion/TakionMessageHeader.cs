using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Takion;

/// <summary>
/// The 13-byte common header at the front of every Takion message (spec §3, wire-validated against
/// <c>rudp_control_setup.pcapng</c>):
/// <code>
/// off 0      u8   base type (low nibble selects the class; 0 = control)
/// off 1..4   u32  verification tag (the peer's connection tag; 0 in the initial INIT)
/// off 5..8   u32  4-byte GMAC tag  (0 on the unencrypted handshake packets)
/// off 9..12  u32  key position     (0 on the unencrypted handshake packets)
/// off 13..   ...  SCTP chunk(s)
/// </code>
/// All multi-byte integers are big-endian. For control packets the GMAC tag and key position live at
/// offsets 5 and 9 respectively - the same class-specific offsets the per-packet crypto uses (§3).
/// </summary>
public readonly struct TakionMessageHeader
{
    public const int Length = 13;
    public const byte BaseTypeControl = 0x00;

    /// <summary>
    /// Base type of the senkusha MTU probe the console sends downstream (wire-observed, cap
    /// session8-wireshark.pcapng frame 180).
    /// </summary>
    public const byte BaseTypeSenkushaMtu = 0x02;

    /// <summary>
    /// Base type of the senkusha echo packet, which the console returns byte-identically (wire-observed, frames
    /// 155-175 of the same capture: ten 548-byte payloads, each echoed unchanged).
    /// </summary>
    public const byte BaseTypeSenkushaEcho = 0x03;

    public TakionMessageHeader(byte baseType, uint verificationTag, uint gmacTag = 0, uint keyPosition = 0)
    {
        BaseType = baseType;
        VerificationTag = verificationTag;
        GmacTag = gmacTag;
        KeyPosition = keyPosition;
    }

    public byte BaseType { get; }
    public uint VerificationTag { get; }
    public uint GmacTag { get; }
    public uint KeyPosition { get; }

    /// <summary>The offset of the SCTP chunk region (also the byte offset the control-class GMAC tag lives at is 5).</summary>
    public const int ChunkOffset = Length;

    public void Write(Span<byte> destination)
    {
        if (destination.Length < Length)
            throw new ArgumentException("Destination too small for a Takion header.", nameof(destination));

        destination[0] = BaseType;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], VerificationTag);
        BinaryPrimitives.WriteUInt32BigEndian(destination[5..], GmacTag);
        BinaryPrimitives.WriteUInt32BigEndian(destination[9..], KeyPosition);
    }

    public static bool TryParse(ReadOnlySpan<byte> packet, out TakionMessageHeader header)
    {
        header = default;
        if (packet.Length < Length)
        {
            return false;
        }

        header = new TakionMessageHeader(
            packet[0],
            BinaryPrimitives.ReadUInt32BigEndian(packet[1..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[5..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[9..]));
        return true;
    }
}

/// <summary>SCTP chunk types carried in a Takion control packet (byte 13), values wire-observed (§8).</summary>
public enum SctpChunkType : byte
{
    Data = 0x00,
    Init = 0x01,
    InitAck = 0x02,
    Sack = 0x03,
    CookieEcho = 0x0a,
    CookieAck = 0x0b,
}
