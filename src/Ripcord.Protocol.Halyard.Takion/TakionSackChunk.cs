using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Takion;

/// <summary>
/// A Takion SACK chunk - standard SCTP SACK, wire-validated against <c>rudp_control_setup.pcapng</c>:
/// <code>
/// [13-byte Takion header, verification tag = the peer's tag]
///   chunk: off 0   u8   type = 0x03 (SACK)
///          off 1   u8   flags = 0x00
///          off 2..3 u16 chunk length (= 16 with no gap/dup blocks)
///          value: off 0..3 u32  cumulative_tsn_ack (highest in-order seq received)
///                 off 4..7 u32  a_rwnd
///                 off 8..9 u16  num_gap_ack_blocks
///                 off 10..11 u16 num_dup_tsns
///                 [gap blocks: n × {u16 start, u16 end}] [dup tsns: n × u32]
/// </code>
/// On this clean-LAN capture every SACK carries zero gap/dup blocks (pure cumulative ack).
/// </summary>
public static class TakionSackChunk
{
    public const int MinChunkLength = 4 + 12;

    /// <summary>Build a cumulative-only SACK (no gap/dup blocks) acking up to <paramref name="cumulativeTsnAck"/>.</summary>
    public static byte[] Build(uint verificationTag, uint cumulativeTsnAck, uint receiveWindow = TakionHandshake.DefaultReceiveWindow)
    {
        var packet = new byte[TakionMessageHeader.Length + MinChunkLength];
        var span = packet.AsSpan();
        new TakionMessageHeader(TakionMessageHeader.BaseTypeControl, verificationTag).Write(span);

        var chunk = span[TakionMessageHeader.Length..];
        chunk[0] = (byte)SctpChunkType.Sack;
        chunk[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(chunk[2..], MinChunkLength);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[4..], cumulativeTsnAck);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[8..], receiveWindow);
        // num_gap_ack_blocks (chunk[12..14]) and num_dup_tsns (chunk[14..16]) remain zero.
        return packet;
    }

    /// <summary>Parsed SACK: the cumulative ack and advertised window (gap/dup blocks exposed as counts).</summary>
    public readonly record struct Parsed(uint CumulativeTsnAck, uint ReceiveWindow, int GapAckBlocks, int DupTsns);

    public static bool TryParse(ReadOnlySpan<byte> packet, out Parsed parsed)
    {
        parsed = default;
        if (!TakionMessageHeader.TryParse(packet, out _))
        {
            return false;
        }

        var chunk = packet[TakionMessageHeader.Length..];
        if (chunk.Length < 4 || chunk[0] != (byte)SctpChunkType.Sack)
        {
            return false;
        }

        int chunkLength = BinaryPrimitives.ReadUInt16BigEndian(chunk[2..]);
        if (chunkLength < MinChunkLength || chunk.Length < chunkLength)
        {
            return false;
        }

        parsed = new Parsed(
            BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(chunk[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(chunk[12..]),
            BinaryPrimitives.ReadUInt16BigEndian(chunk[14..]));
        return true;
    }
}
