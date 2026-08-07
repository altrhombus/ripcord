using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Common.Streaming;

/// <summary>
/// The v1 A/V data-packet header, bit-exact per docs/protocol/ps5-remoteplay-v1-spec.md §6.1 (all
/// multi-byte integers big-endian). This is the confirmed layout that supersedes the earlier tentative
/// framing: the key position (offset 14) drives the per-packet crypto, and the 4-byte GMAC tag sits at
/// offset 10.
///
/// <code>
/// off 0      u8   low nibble = type (2=video, 3=audio, 0x12=FEC); bit 4 = extended-header flag
/// off 1..2   u16  packet_index          (per-packet sequence, +1 per packet)
/// off 3..4   u16  frame_index
/// off 5..8   u32  bits[31:21] unit_index | bits[20:10] total_units-1 | bits[9:0] parity_units
/// off 9      u8   codec
/// off 10..13 u32  4-byte GMAC tag (zeroed for the GMAC computation)
/// off 14..17 u32  key position (running byte counter; drives the crypto nonce - NOT a timestamp)
/// off 18..   ...  media payload; +3 bytes for video (size_extension u16 + adaptive_stream_index),
///                 +3 more if the extended-header flag is set
/// </code>
/// </summary>
public readonly struct HalyardStreamHeader
{
    /// <summary>Bytes 0..17 are common to every A/V packet before the type-specific payload prefix.</summary>
    public const int BaseLength = 18;

    public const byte TypeVideo = 0x02;
    public const byte TypeAudio = 0x03;

    // There is deliberately no TypeFec. A raw byte-0 of 0x12 is often described as "the FEC type", but 0x12 is
    // low-nibble 2 (video) with bit 4 (extended header) set — and since Type is masked to the low nibble, a
    // `Type == 0x12` comparison is unconditionally false. A `TypeFec = 0x12` constant did sit here unused,
    // which is a silently-always-false check waiting to be written. Parity units are not a packet type at all:
    // they are video packets whose unit index falls at or above SourceUnits. Use IsParityUnit.

    public const int TagOffset = 10;
    public const int KeyPositionOffset = 14;

    public HalyardStreamHeader(
        byte type, bool hasExtendedHeader, ushort packetIndex, ushort frameIndex,
        int unitIndex, int totalUnits, int parityUnits, byte codec, uint keyPosition)
    {
        Type = type;
        HasExtendedHeader = hasExtendedHeader;
        PacketIndex = packetIndex;
        FrameIndex = frameIndex;
        UnitIndex = unitIndex;
        TotalUnits = totalUnits;
        ParityUnits = parityUnits;
        Codec = codec;
        KeyPosition = keyPosition;
    }

    public byte Type { get; }
    public bool HasExtendedHeader { get; }
    public ushort PacketIndex { get; }
    public ushort FrameIndex { get; }
    public int UnitIndex { get; }
    public int TotalUnits { get; }
    public int ParityUnits { get; }
    public byte Codec { get; }
    public uint KeyPosition { get; }

    public bool IsVideo => Type == TypeVideo;
    public bool IsAudio => Type == TypeAudio;

    /// <summary>Number of source units in the frame (total minus FEC units).</summary>
    public int SourceUnits => TotalUnits - ParityUnits;

    /// <summary>
    /// True when this unit is one of the frame's Reed-Solomon parity units rather than a source unit — which
    /// is a function of the unit index, not of the packet type. Parity units carry no 2-byte size extension:
    /// they are already exactly the frame's coded unit length.
    /// </summary>
    public bool IsParityUnit => UnitIndex >= SourceUnits;

    /// <summary>
    /// Where the (encrypted) payload begins: the 18-byte base, then a type-specific prefix, then the optional
    /// extended header. Video adds 3 (size_extension u16 + adaptive_stream_index). Audio adds 2 on the A/V
    /// layout this protocol version uses (a 1-byte "unknown" field plus a 1-byte haptics indicator); missing
    /// the haptics byte misaligns the audio CTR keystream by one and Opus decodes garbage.
    /// </summary>
    public int PayloadOffset => BaseLength + (IsVideo ? 3 : 2) + (HasExtendedHeader ? 3 : 0);

    public static bool TryParse(ReadOnlySpan<byte> packet, out HalyardStreamHeader header)
    {
        header = default;
        if (packet.Length < BaseLength)
        {
            return false;
        }

        byte byte0 = packet[0];
        byte type = (byte)(byte0 & 0x0f);
        bool hasExtendedHeader = (byte0 & 0x10) != 0;
        ushort packetIndex = BinaryPrimitives.ReadUInt16BigEndian(packet[1..]);
        ushort frameIndex = BinaryPrimitives.ReadUInt16BigEndian(packet[3..]);
        // The bytes-5..8 packed field is laid out differently for video and audio. Video packs three
        // 11/11/10-bit fields; audio packs byte-wide fields (unit_index in the top byte, total_units-1 in the
        // next). Parsing audio with the video layout yields nonsense unit counts (e.g. 137), which is why the
        // audio unit structure went unnoticed until now.
        uint packed = BinaryPrimitives.ReadUInt32BigEndian(packet[5..]);
        int unitIndex, totalUnits, parityUnits;
        if (type == TypeVideo)
        {
            unitIndex = (int)(packed >> 21);
            totalUnits = (int)((packed >> 10) & 0x7ff) + 1;
            parityUnits = (int)(packed & 0x3ff);
        }
        else
        {
            unitIndex = (int)((packed >> 24) & 0xff);
            totalUnits = (int)((packed >> 16) & 0xff) + 1;
            // The low 16 bits encode a firmware-specific source/parity/unit-size packing that does not match
            // the earlier protocol revision's layout; the demuxer derives the unit size from the payload
            // length and total_units instead, so this raw value is retained only for diagnostics.
            parityUnits = (int)(packed & 0xffff);
        }
        byte codec = packet[9];
        uint keyPosition = BinaryPrimitives.ReadUInt32BigEndian(packet[KeyPositionOffset..]);

        header = new HalyardStreamHeader(
            type, hasExtendedHeader, packetIndex, frameIndex,
            unitIndex, totalUnits, parityUnits, codec, keyPosition);
        return true;
    }
}
