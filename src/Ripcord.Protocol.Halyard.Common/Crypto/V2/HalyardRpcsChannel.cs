using System.Buffers.Binary;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V2;

/// <summary>
/// The v2 <b>RPCS</b> during-stream control channel framing (spec §9, confirmed byte-exact against our own
/// captures 003/004). After <c>/sess/ctrl</c> the control TCP connection (LAN :9295; the :9303 RUDP association
/// on the rendezvous route) stays open and carries length-prefixed, GCM-sealed messages:
///
/// <code>wire = "RPCS" ‖ be32(inner_len + 16) ‖ ciphertext ‖ tag16</code>
///
/// <para>Each message is AES-128-GCM under the Path-A control keys (<see cref="IHalyardV2ControlCrypto"/>: client
/// seals with <c>sk_0x09</c>, the console with <c>sk_0x08</c>), with the 8-byte header as the AAD and the
/// per-direction connection counter <b>continuing from the ctrl message</b> (which was counter 0 on this
/// connection), so the first RPCS message in each direction is counter 1. The length field counts the ciphertext
/// plus the 16-byte tag — i.e. everything after the header — so a reader frames by reading the 8-byte header,
/// then that many more bytes.</para>
///
/// <para>This is the transport only: the framing + the GCM envelope + the counter discipline, which are the
/// confirmed part. The inner message <em>semantics</em> (which RPCS types to send when — keepalive, the probe
/// report, …) are partly <c>[X]</c> (RPCS-SEMANTICS.md) and are modelled by the caller, not here.</para>
/// </summary>
public sealed class HalyardRpcsChannel
{
    /// <summary>The 4-byte channel magic that opens every RPCS frame.</summary>
    public static ReadOnlySpan<byte> Magic => "RPCS"u8;

    /// <summary>The fixed header: <c>"RPCS"</c> (4) + <c>be32(inner_len + 16)</c> (4).</summary>
    public const int HeaderLength = 8;

    /// <summary>The GCM tag length appended to every frame.</summary>
    public const int TagLength = 16;

    private readonly IHalyardV2ControlCrypto _crypto;
    private ulong _sendCounter;
    private ulong _recvCounter;

    /// <summary>
    /// Wrap an established control crypto. The counters default to 1 — the value that continues from the ctrl
    /// message's 0 on the same connection — but are overridable for tests and for any path whose ctrl counter
    /// differs.
    /// </summary>
    public HalyardRpcsChannel(IHalyardV2ControlCrypto crypto, ulong startSendCounter = 1, ulong startRecvCounter = 1)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _sendCounter = startSendCounter;
        _recvCounter = startRecvCounter;
    }

    /// <summary>The next client→console counter that <see cref="Seal"/> will use.</summary>
    public ulong SendCounter => _sendCounter;

    /// <summary>The next console→client counter that <see cref="Open"/> will expect.</summary>
    public ulong RecvCounter => _recvCounter;

    /// <summary>
    /// Frame and seal one outgoing (client→console) RPCS message, advancing the send counter. The returned buffer
    /// is the complete wire frame (header + ciphertext + tag).
    /// </summary>
    public byte[] Seal(ReadOnlySpan<byte> inner)
    {
        byte[] header = BuildHeader(inner.Length + TagLength);
        byte[] ciphertextAndTag = _crypto.SealClientToConsole(inner, header, _sendCounter);
        _sendCounter++;

        byte[] frame = new byte[HeaderLength + ciphertextAndTag.Length];
        header.CopyTo(frame, 0);
        ciphertextAndTag.CopyTo(frame, HeaderLength);
        return frame;
    }

    /// <summary>
    /// Verify and decrypt one complete incoming (console→client) RPCS frame, advancing the receive counter only on
    /// success. Returns the inner plaintext, or <see langword="null"/> if the frame is malformed or does not
    /// authenticate (the counter is then left untouched, so a caller can surface the fault without desyncing).
    /// </summary>
    public byte[]? Open(ReadOnlySpan<byte> frame)
    {
        if (!TryGetInnerLength(frame, out int innerPlusTag) || frame.Length != HeaderLength + innerPlusTag)
            return null;

        ReadOnlySpan<byte> header = frame[..HeaderLength];
        byte[]? plaintext = _crypto.OpenConsoleToClient(frame[HeaderLength..], header, _recvCounter);
        if (plaintext is null)
            return null;

        _recvCounter++;
        return plaintext;
    }

    /// <summary>
    /// Read an RPCS header and report the full frame length (<see cref="HeaderLength"/> + the declared body), so a
    /// stream reader knows how many bytes to pull before calling <see cref="Open"/>. Returns <see langword="false"/>
    /// if the span is too short or does not begin with the <see cref="Magic"/>.
    /// </summary>
    public static bool TryGetFrameLength(ReadOnlySpan<byte> header, out int frameLength)
    {
        frameLength = 0;
        if (!TryGetInnerLength(header, out int innerPlusTag))
            return false;
        frameLength = HeaderLength + innerPlusTag;
        return true;
    }

    // The header's be32 counts the ciphertext + tag (everything after the header). A valid frame carries at least
    // the 16-byte tag, so a body shorter than that is malformed.
    private static bool TryGetInnerLength(ReadOnlySpan<byte> frameOrHeader, out int innerPlusTag)
    {
        innerPlusTag = 0;
        if (frameOrHeader.Length < HeaderLength || !frameOrHeader[..4].SequenceEqual(Magic))
            return false;
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(frameOrHeader.Slice(4, 4));
        if (declared < TagLength || declared > int.MaxValue - HeaderLength)
            return false;
        innerPlusTag = (int)declared;
        return true;
    }

    private static byte[] BuildHeader(int innerPlusTag)
    {
        byte[] header = new byte[HeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)innerPlusTag);
        return header;
    }
}
