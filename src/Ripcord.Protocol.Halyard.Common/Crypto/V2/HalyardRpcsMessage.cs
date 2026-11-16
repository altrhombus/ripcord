using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V2;

/// <summary>
/// One RPCS message's inner plaintext — what sits inside the GCM envelope of a <see cref="HalyardRpcsChannel"/>
/// frame (v2 spec §9, confirmed byte-exact from session 004's own decrypt):
///
/// <code>be64 seq | be32 payload_len | be16 cmd | be16 flags | payload[payload_len]</code>
///
/// <para>The <see cref="Seq"/> equals the per-direction GCM counter the frame is sealed under — the console sets
/// it on incoming messages and the client sets it on outgoing ones — so <see cref="HalyardRpcsChannel"/> fills it
/// from its own send counter rather than leaving it to the caller. <see cref="Flags"/> was 0 in every observed
/// message. A command with the <see cref="HalyardRpcsCommands.ReplyBit"/> (<c>0x8000</c>) set is a reply to the
/// request of the same low command (e.g. <c>0x8910</c> answers <c>0x0910</c>).</para>
/// </summary>
public readonly record struct HalyardRpcsMessage(ulong Seq, ushort Cmd, ushort Flags, byte[] Payload)
{
    /// <summary>The fixed inner header: <c>be64 seq</c> + <c>be32 payload_len</c> + <c>be16 cmd</c> + <c>be16 flags</c>.</summary>
    public const int HeaderLength = 16;

    /// <summary>Whether this is a reply (the <see cref="HalyardRpcsCommands.ReplyBit"/> is set in <see cref="Cmd"/>).</summary>
    public bool IsReply => (Cmd & HalyardRpcsCommands.ReplyBit) != 0;

    /// <summary>Serialize to the inner plaintext (header + payload), ready to seal into an RPCS frame.</summary>
    public byte[] Encode()
    {
        byte[] inner = new byte[HeaderLength + Payload.Length];
        BinaryPrimitives.WriteUInt64BigEndian(inner, Seq);
        BinaryPrimitives.WriteUInt32BigEndian(inner.AsSpan(8), (uint)Payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(inner.AsSpan(12), Cmd);
        BinaryPrimitives.WriteUInt16BigEndian(inner.AsSpan(14), Flags);
        Payload.CopyTo(inner.AsSpan(HeaderLength));
        return inner;
    }

    /// <summary>
    /// Parse a decrypted inner plaintext. Fails if it is shorter than the header or if the declared
    /// <c>payload_len</c> does not match the bytes present (a malformed or truncated message).
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> inner, out HalyardRpcsMessage message)
    {
        message = default;
        if (inner.Length < HeaderLength)
            return false;

        ulong seq = BinaryPrimitives.ReadUInt64BigEndian(inner);
        uint payloadLen = BinaryPrimitives.ReadUInt32BigEndian(inner.Slice(8, 4));
        ushort cmd = BinaryPrimitives.ReadUInt16BigEndian(inner.Slice(12, 2));
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(inner.Slice(14, 2));
        if ((long)HeaderLength + payloadLen != inner.Length)
            return false;

        message = new HalyardRpcsMessage(seq, cmd, flags, inner[HeaderLength..].ToArray());
        return true;
    }
}
