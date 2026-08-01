using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Takion;

/// <summary>
/// A Takion reliable DATA chunk carrying (a fragment of) a control-plane <c>ControlMessage</c> protobuf
/// (spec §8). Layout, pinned by parsing captured DATA packets against the generated schema:
/// <code>
/// [13-byte Takion header] [DATA chunk]
///   chunk: off 0   u8   type = 0x00 (DATA)
///          off 1   u8   flags (bit 0 = end-of-message)
///          off 2..3 u16 chunk length (= 4 + value length)
///          value: off 0..3 u32  seq_num (starts at the connection tag, +1 per DATA)
///                 off 4..5 u16  channel (a stream id; client uses per-class channels, server uses 0)
///                 off 6..  reserved, then payload
/// </code>
/// The payload offset depends on whether the chunk is the <b>first</b> fragment of a message: the first (or
/// only) fragment reserves 3 bytes so its payload starts at offset <b>9</b>; a continuation fragment reserves
/// 2 bytes so its payload starts at offset <b>8</b> (wire-confirmed on a fragmented SESSION_REQUEST). The
/// end-of-message flag marks the last fragment; "first" is positional, so <see cref="TakionMessageReassembler"/>
/// tracks it from its own state.
/// </summary>
public static class TakionDataChunk
{
    /// <summary>Chunk flags: bit 0 (0x01) is the SCTP "ending" bit - set on the last (or only) fragment.</summary>
    public const byte EndOfMessageFlag = 0x01;
    public const int SeqOffset = 0;
    public const int ChannelOffset = 4;

    /// <summary>Payload offset within the value for the first/only fragment (seq 4 + channel 2 + reserved 3).</summary>
    public const int FirstFragmentOffset = 9;

    /// <summary>Payload offset within the value for a continuation fragment (seq 4 + channel 2 + reserved 2).</summary>
    public const int ContinuationOffset = 8;

    /// <summary>Well-known client channels (SCTP stream ids), from the wire.</summary>
    public const ushort ChannelSession = 0x0001;
    public const ushort ChannelBandwidth = 0x0008;
    public const ushort ChannelStreamInfo = 0x0009;   // STREAM_INFO_ACK (wire-confirmed)
    public const ushort ChannelProtocolVersion = 0x0015;

    /// <summary>
    /// Build a full DATA packet carrying <paramref name="payload"/> (a whole ControlMessage protobuf, or a
    /// fragment of one). <paramref name="endOfMessage"/> sets the ending bit (false for a non-final fragment);
    /// <paramref name="firstFragment"/> selects the 3-byte (first/only) vs 2-byte (continuation) reserved region.
    /// </summary>
    public static byte[] Build(
        uint verificationTag,
        uint seq,
        ushort channel,
        ReadOnlySpan<byte> payload,
        bool endOfMessage = true,
        bool firstFragment = true,
        uint gmacTag = 0,
        uint keyPosition = 0)
    {
        int payloadOffset = firstFragment ? FirstFragmentOffset : ContinuationOffset;
        int valueLength = payloadOffset + payload.Length;
        int chunkLength = 4 + valueLength;
        var packet = new byte[TakionMessageHeader.Length + chunkLength];
        var span = packet.AsSpan();

        new TakionMessageHeader(TakionMessageHeader.BaseTypeControl, verificationTag, gmacTag, keyPosition).Write(span);

        var chunk = span[TakionMessageHeader.Length..];
        chunk[0] = (byte)SctpChunkType.Data;
        chunk[1] = endOfMessage ? EndOfMessageFlag : (byte)0x00;
        BinaryPrimitives.WriteUInt16BigEndian(chunk[2..], (ushort)chunkLength);

        var value = chunk[4..];
        BinaryPrimitives.WriteUInt32BigEndian(value[SeqOffset..], seq);
        BinaryPrimitives.WriteUInt16BigEndian(value[ChannelOffset..], channel);
        // reserved (2 or 3 bytes) already zero
        payload.CopyTo(value[payloadOffset..]);
        return packet;
    }

    /// <summary>
    /// One parsed DATA chunk. <see cref="Value"/> is the whole chunk value (from seq); the reassembler slices
    /// <see cref="FirstPayload"/> or <see cref="ContinuationPayload"/> depending on the fragment's position.
    /// </summary>
    public readonly record struct Parsed(uint Seq, ushort Channel, bool EndOfMessage, ReadOnlyMemory<byte> Value)
    {
        /// <summary>Payload when this is the first/only fragment of a message (offset 9).</summary>
        public ReadOnlyMemory<byte> FirstPayload => Value[FirstFragmentOffset..];

        /// <summary>Payload when this is a continuation fragment (offset 8).</summary>
        public ReadOnlyMemory<byte> ContinuationPayload => Value[ContinuationOffset..];

        /// <summary>Convenience for callers dealing only in single-chunk messages (same as <see cref="FirstPayload"/>).</summary>
        public ReadOnlyMemory<byte> Payload => FirstPayload;
    }

    /// <summary>Parse a DATA packet. Returns false if the packet is not a well-formed DATA chunk.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> packet, out Parsed parsed)
    {
        parsed = default;

        ReadOnlySpan<byte> span = packet.Span;
        if (!TakionMessageHeader.TryParse(span, out _))
        {
            return false;
        }

        var chunk = span[TakionMessageHeader.Length..];
        if (chunk.Length < 4 || chunk[0] != (byte)SctpChunkType.Data)
        {
            return false;
        }

        byte flags = chunk[1];
        int chunkLength = BinaryPrimitives.ReadUInt16BigEndian(chunk[2..]);
        if (chunkLength < 4 + ContinuationOffset || chunk.Length < chunkLength)
        {
            return false;
        }

        int valueStart = TakionMessageHeader.Length + 4;
        int valueLength = chunkLength - 4;
        var value = packet.Slice(valueStart, valueLength);
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(value.Span[SeqOffset..]);
        ushort channel = BinaryPrimitives.ReadUInt16BigEndian(value.Span[ChannelOffset..]);
        parsed = new Parsed(seq, channel, (flags & EndOfMessageFlag) != 0, value);
        return true;
    }
}
