using System.Security.Cryptography;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V1;

/// <summary>
/// The real v1 session crypto: composes the control KDF/field cipher (<see cref="HalyardControlKdf"/>,
/// <see cref="HalyardControlFieldCrypto"/>) and the stream key schedule + per-packet crypto
/// (<see cref="HalyardStreamKeySchedule"/>, <see cref="HalyardPacketCrypto"/>) behind the session seam.
///
/// The extracted interop constants (KDF tables, context keys) are injected via
/// <see cref="HalyardControlSecrets"/> - supplied from the gitignored dirty room, never committed.
/// </summary>
public sealed class HalyardV1SessionCrypto : IHalyardSessionCrypto, IDisposable
{
    private readonly HalyardControlSecrets _secrets;
    private readonly HalyardControlKdf _kdf;

    private HalyardControlFieldCrypto? _control;
    private ECDiffieHellman? _ephemeral;
    private HalyardPacketCrypto? _send;    // client -> server (outgoing / input)
    private HalyardPacketCrypto? _receive; // server -> client (incoming / A/V)

    public HalyardV1SessionCrypto(HalyardControlSecrets secrets)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _kdf = new HalyardControlKdf(secrets);
    }

    public bool IsControlEstablished => _control is not null;
    public bool IsStreamEstablished => _send is not null && _receive is not null;

    // ---- control plane ----

    public void EstablishControl(in HalyardControlKeyMaterial material)
    {
        _control = HalyardControlFieldCrypto.FromKdf(
            _kdf, _secrets.ContextKeys,
            material.Nonce.Span, material.Companion.Span,
            material.CodecSelector, material.VersionSelector);
    }

    public byte[] EncryptControlField(ulong counter, ReadOnlySpan<byte> plaintext)
        => Control.EncryptField(counter, plaintext);

    public byte[] DecryptControlField(ulong counter, ReadOnlySpan<byte> ciphertext)
        => Control.DecryptField(counter, ciphertext);

    public byte[] CryptStreaminfo(ulong counter, ReadOnlySpan<byte> data)
        => Control.StreaminfoCrypt(counter, data);

    private HalyardControlFieldCrypto Control =>
        _control ?? throw new InvalidOperationException("Control crypto not established (call EstablishControl first).");

    // ---- stream key agreement ----

    public byte[] GenerateEphemeralPublicKey(int protocolVersion = 0)
    {
        _ephemeral?.Dispose();
        var curve = HalyardStreamKeySchedule.CurveForVersion(protocolVersion);
        var (keyPair, publicKey) = HalyardStreamKeySchedule.GenerateKeyPair(curve);
        _ephemeral = keyPair;
        return publicKey;
    }

    public byte[] ComputeEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey)
        => HalyardStreamKeySchedule.ComputeEcdhSignature(handshakeKey, publicKey);

    public bool VerifyEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signature)
        => HalyardStreamKeySchedule.VerifyEcdhSignature(handshakeKey, publicKey, signature);

    public bool TryEstablishStream(ReadOnlySpan<byte> peerPublicKey, ReadOnlySpan<byte> handshakeKey)
    {
        if (_ephemeral is null)
            throw new InvalidOperationException("Call GenerateEphemeralPublicKey before establishing the stream.");

        byte[] sharedSecret;
        try
        {
            sharedSecret = HalyardStreamKeySchedule.DeriveSharedSecret(_ephemeral, peerPublicKey);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return false;
        }

        var sendKeys = HalyardStreamKeySchedule.DeriveDirection(sharedSecret, handshakeKey, HalyardStreamKeySchedule.DirectionClientToServer);
        var recvKeys = HalyardStreamKeySchedule.DeriveDirection(sharedSecret, handshakeKey, HalyardStreamKeySchedule.DirectionServerToClient);
        _send = new HalyardPacketCrypto(sendKeys);
        _receive = new HalyardPacketCrypto(recvKeys);
        return true;
    }

    // ---- per-packet stream crypto ----

    public bool TryOpenPacket(Span<byte> packet, in HalyardPacketLayout layout)
    {
        var receive = _receive;
        if (receive is null)
            return false; // stream not yet established - drop rather than throw
        if (!receive.VerifyPacket(layout.KeyPos, packet, layout.TagOffset))
            return false;

        // Decrypt the payload genuinely in place (AES-CTR is symmetric) — the previous form allocated a result
        // buffer and copied it back, per packet.
        receive.CryptPayloadInPlace(layout.KeyPos, packet[layout.PayloadOffset..]);
        return true;
    }

    public void SealPacket(Span<byte> packet, in HalyardPacketLayout layout)
    {
        var send = _send ?? throw new InvalidOperationException("Stream crypto not established.");

        // Encrypt the payload in place, then MAC the whole (encrypted) packet and write the tag.
        var encrypted = send.CryptPayload(layout.KeyPos, packet[layout.PayloadOffset..]);
        encrypted.CopyTo(packet[layout.PayloadOffset..]);

        var tag = send.ComputeTag(layout.KeyPos, packet, layout.TagOffset);
        tag.CopyTo(packet.Slice(layout.TagOffset, HalyardPacketCrypto.TagLength));
    }

    // Control DATA header (base-type 0x00): GMAC tag at [5..8], key position at [9..12].
    private const int ControlTagOffset = 5;
    private const int ControlKeyPosOffset = 9;
    private ulong _sendKeyPos; // advancing local key position for outgoing (control) DATA + SACKs
    private readonly Lock _sendKeyPosGate = new();

    public void SealControlMessage(Span<byte> packet)
    {
        var send = _send;
        if (send is null || packet.Length < TakionControlHeaderLength)
        {
            return; // pre-key-agreement (or malformed) — leave unauthenticated, which is correct before keys
        }

        // Reserve a key position; outgoing control (DATA on one thread, SACKs on the receive-loop thread)
        // may seal concurrently, so serialise the advance to keep positions/nonces distinct.
        ulong keyPos;
        int aligned = packet.Length + (HalyardPacketCrypto.BlockSize - packet.Length % HalyardPacketCrypto.BlockSize) % HalyardPacketCrypto.BlockSize;
        lock (_sendKeyPosGate)
        {
            keyPos = _sendKeyPos;
            _sendKeyPos += (ulong)aligned;
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(ControlKeyPosOffset, 4), (uint)keyPos);
        // Control GMAC zeroes both the tag and the key_pos field in the AAD; A/V zeroes only the tag.
        // PROVENANCE, and the two halves differ — keep them apart:
        //  * The field OFFSETS are [V] as of 2026-07-29: control tag@5 / key_pos@9 confirmed across 2528
        //    type-0 packets in cap47 (bytes 5-8 random, 2516/2528 distinct; bytes 9-12 all multiples of 16,
        //    2508 distinct). Congestion tag@7 / key_pos@11 confirmed the same way over 474 packets.
        //  * The AAD RULE — that control zeroes the key_pos field as well as the tag — is [V] as of
        //    2026-08-02. Recomputed offline over cap3 (the session whose stream keys were dumped from the
        //    same connection attempt): of 727 authenticated type-0 packets, 364 client->server and 363
        //    server->client, zeroing tag+key_pos reproduces the on-wire tag on 727/727. Zeroing the tag
        //    alone matches only the 2 packets (one per direction) whose key_pos is 0, where the two rules
        //    are byte-identical and so prove nothing. A wrong AAD cannot coincidentally match a 32-bit tag
        //    727 times. The remaining 67 type-0 packets carry an all-zero tag — pre-key INIT/COOKIE
        //    handshake, unauthenticated — and are excluded rather than counted as failures.
        byte[] tag = send.ComputeTag(keyPos, packet, ControlTagOffset, zeroKeyPos: true);
        tag.CopyTo(packet.Slice(ControlTagOffset, HalyardPacketCrypto.TagLength));
    }

    // Congestion packet (base-type 0x05): 4-byte GMAC tag at [7..10], key position at [0xb..0xe]. Because the
    // key position immediately follows the tag, zeroing tag+key_pos (zeroKeyPos: true) covers offsets [7..15).
    //
    // NOTE the provenance gap, and do not let the control result above be read as covering this: the control
    // AAD rule is [V] (verified over 727 real packets), but congestion is [X] — applied here by analogy with
    // control, not measured. cap3 is the only capture pairing traffic with dumped keys and it is 100% type-0,
    // so it carries no congestion packet to test. Settling this needs a capture that reaches streaming with
    // correlated keys; the offsets themselves are [V] (474 packets in cap47), only the AAD rule is assumed.
    private const int CongestionTagOffset = 7;
    private const int CongestionKeyPosOffset = 0xb;
    private const int CongestionPacketLength = 15;

    public void SealCongestionPacket(Span<byte> packet)
    {
        var send = _send;
        if (send is null || packet.Length < CongestionPacketLength)
        {
            return; // pre-key-agreement — leave unauthenticated (never sent before keys exist anyway)
        }

        // Share the outgoing key-position counter with control DATA/SACKs so no nonce is ever reused.
        ulong keyPos;
        int aligned = packet.Length + (HalyardPacketCrypto.BlockSize - packet.Length % HalyardPacketCrypto.BlockSize) % HalyardPacketCrypto.BlockSize;
        lock (_sendKeyPosGate)
        {
            keyPos = _sendKeyPos;
            _sendKeyPos += (ulong)aligned;
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(CongestionKeyPosOffset, 4), (uint)keyPos);
        byte[] tag = send.ComputeTag(keyPos, packet, CongestionTagOffset, zeroKeyPos: true);
        tag.CopyTo(packet.Slice(CongestionTagOffset, HalyardPacketCrypto.TagLength));
    }

    // Feedback packet (base-type 6 = state, 1 = history): 4-byte key position at [4..8], 4-byte GMAC tag at
    // [8..12], payload from [0xc]. Unlike control/congestion, the GMAC zeroes ONLY the tag (like A/V), and the
    // payload IS AES-CTR encrypted.
    private const int FeedbackKeyPosOffset = 4;
    private const int FeedbackTagOffset = 8;

    public void SealFeedbackPacket(Span<byte> packet, int payloadOffset)
    {
        var send = _send;
        if (send is null || packet.Length < payloadOffset)
        {
            return; // pre-key-agreement — never sent before keys exist
        }

        // Share the outgoing key-position counter with control DATA/SACKs and congestion so no nonce repeats.
        ulong keyPos;
        int aligned = packet.Length + (HalyardPacketCrypto.BlockSize - packet.Length % HalyardPacketCrypto.BlockSize) % HalyardPacketCrypto.BlockSize;
        lock (_sendKeyPosGate)
        {
            keyPos = _sendKeyPos;
            _sendKeyPos += (ulong)aligned;
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(FeedbackKeyPosOffset, 4), (uint)keyPos);

        // Encrypt the payload genuinely in place, then MAC the whole (encrypted) packet with only the tag zeroed.
        send.CryptPayloadInPlace(keyPos, packet[payloadOffset..]);
        byte[] tag = send.ComputeTag(keyPos, packet, FeedbackTagOffset, zeroKeyPos: false);
        tag.CopyTo(packet.Slice(FeedbackTagOffset, HalyardPacketCrypto.TagLength));
    }

    private const int TakionControlHeaderLength = 13;

    public void Dispose()
    {
        _ephemeral?.Dispose();
        _ephemeral = null;

        // Each direction owns a cached AES block cipher for its payload key.
        _send?.Dispose();
        _receive?.Dispose();
    }
}
