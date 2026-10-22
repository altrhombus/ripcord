using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;
using Ripcord.Core.Net.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V2;

/// <summary>
/// The v2 (PS5) session crypto. A <b>seam-and-stub</b> at present (CLAUDE.md, "Prefer this
/// seam-and-stub shape for new unsolved work"): the shape is fixed and the design is captured, and the real
/// transforms land phase by phase, each gated by the known-answer vectors in docs/protocol/captures
/// (V2-STREAM-VALIDATION.md). It is wired to nothing live until the control-plane and state machine are done,
/// so v1 behaviour is unchanged.
///
/// <para><b>What goes where.</b> v2 is a set of deltas on v1:</para>
/// <list type="bullet">
///   <item><description><b>Control plane</b> — the AES-128-GCM envelope of
///   <see cref="IHalyardV2ControlCrypto"/> (Path A) replaces v1's per-field CFB. The v1 field methods on
///   <see cref="IHalyardSessionCrypto"/> therefore throw here.</description></item>
///   <item><description><b>launchSpec</b> — still AES-128-OFB under the v1 control KDF (M = KDF(RP-Nonce,
///   RP-Key)), counter 0, so <see cref="CryptStreaminfo"/> is reused (Phase 1).</description></item>
///   <item><description><b>Stream key agreement</b> — the v1 schedule with the per-session secret <c>X</c> in the
///   <c>handshakeKey</c> role (<see cref="HalyardStreamKeySchedule"/>), so the agreement methods carry over
///   (Phase 2, implemented here). <c>X</c> arrives as the <c>handshakeKey</c> argument exactly as v1's launchSpec
///   X did.</description></item>
///   <item><description><b>Per-packet</b> — v2 shifts the A/V header offsets (tag@18, key_pos@22, the caller's
///   <see cref="HalyardPacketLayout"/>) and rotates the GMAC key one window earlier
///   (<see cref="HalyardGmacWindowRule.FromKeyPosMinusOne"/>); AES-CTR is the v1 little-endian block counter
///   unchanged. So the per-packet methods reuse <see cref="HalyardPacketCrypto"/> with the v2 window rule
///   (Phase 2, implemented here for the s2c A/V receive path + key agreement).</description></item>
/// </list>
///
/// <para><b>How <c>X</c> is produced — SOLVED.</b> <c>X</c> is simply the v1 <c>handshakeKey</c>: 16
/// <b>client-random</b> bytes the client generates, carries base64 in the launchSpec, and feeds here. It is not
/// derived from <c>customData1</c> or anything else — static RE of the vendor's launchSpec builder
/// (<c>FUN_9ee50</c>: a no-input RNG callback, osslRAND fallback) and three live v2 sessions confirm it. The
/// console "holds" X only because it received it in the launchSpec (then signs its stream pubkey with it). This
/// class consumes whatever the caller supplies as <c>handshakeKey</c>, exactly as v1 does; the earlier belief
/// that X was an unresolved per-session derivation (the <c>customData1</c> seal line) was wrong — that path
/// carries the registration seed, a separate value.</para>
///
/// <para>The c2s send-authentication methods (<c>SealControlMessage</c>/<c>SealCongestionPacket</c>/
/// <c>SealFeedbackPacket</c>) are implemented (Phase 2): each is a v1 layout under the v1 client->console key,
/// with the v2 per-format header offsets and the v2 GMAC window rule. They were confirmed byte-exact c2s against
/// our own captures 003/004 (V2-SPEC §; the KAT fixtures live in the captures tree). The remaining v2 work is
/// the live wiring (running the GCM control handshake and selecting this crypto in the session path), not the
/// transforms.</para>
/// </summary>
public sealed class HalyardV2SessionCrypto : IHalyardSessionCrypto, IHalyardV2ControlCrypto, IDisposable
{
    private const string NotYet = "HalyardV2SessionCrypto is a seam-and-stub; this path is not implemented yet.";

    // Held for the launchSpec KDF (M) and the per-direction stream schedule, exactly as the v1 crypto holds it.
    private readonly HalyardControlSecrets _secrets;
    private readonly HalyardControlKdf _kdf;

    // The launchSpec cipher is v1's unchanged: AES-128-OFB under M = KDF(RP-Nonce, RP-Key), IV keyed by the
    // selectorOne context key. The v2 launchSpec differs from v1 only by counter 0 — it still carries the same
    // client-random handshakeKey (it is NOT dropped; see the class remarks) — so v2 reuses the v1 field crypto.
    private HalyardControlFieldCrypto? _field;

    // Path A (control plane). The purposes are the info-string's second byte: 0x09 client->console, 0x08 the
    // other way (spec §4). The GCM tag is the full 16 bytes.
    private const byte PurposeClientToConsole = 0x09;
    private const byte PurposeConsoleToClient = 0x08;
    private const int GcmTagLength = 16;

    private ECDiffieHellman? _controlEcdh;
    private HalyardStreamKeySchedule.DirectionKeys? _skClientToConsole; // sk_9: AesKey = sk[0:16], BaseIv = sk[16:32]
    private HalyardStreamKeySchedule.DirectionKeys? _skConsoleToClient; // sk_8

    // Stream plane (Phase 2): its own ephemeral ECDH, separate from the control plane's, and the per-direction
    // packet crypto. v2 rotates the GMAC key one window earlier than v1 (confirmed on s2c A/V).
    private const HalyardGmacWindowRule StreamWindowRule = HalyardGmacWindowRule.FromKeyPosMinusOne;
    private ECDiffieHellman? _streamEcdh;
    private HalyardPacketCrypto? _send;    // client -> console (feedback/input)
    private HalyardPacketCrypto? _receive; // console -> client (A/V)

    public HalyardV2SessionCrypto(HalyardControlSecrets secrets)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _kdf = new HalyardControlKdf(_secrets);
    }

    // ---- IHalyardSessionCrypto: control plane ----

    // v2 does NOT field-encrypt the control plane; it uses the GCM envelope on IHalyardV2ControlCrypto. These
    // are a deliberate not-supported, distinct from the phased "not implemented yet" below.
    private const string NoFieldCrypto =
        "v2 has no per-field control crypto; use the IHalyardV2ControlCrypto GCM envelope.";

    /// <summary>
    /// Establish the control KDF material (M) used for the launchSpec OFB — the v1 derivation, reused unchanged
    /// by v2. This is NOT the GCM control plane (that is <see cref="EstablishControl(ReadOnlySpan{byte},
    /// ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> on <see cref="IHalyardV2ControlCrypto"/>); it only sets up the
    /// launchSpec cipher, so v2 needs both.
    /// </summary>
    public void EstablishControl(in HalyardControlKeyMaterial material)
        => _field = HalyardControlFieldCrypto.FromKdf(
            _kdf, _secrets.ContextKeys,
            material.Nonce.Span, material.Companion.Span,
            material.CodecSelector, material.VersionSelector);

    bool IHalyardSessionCrypto.IsControlEstablished => _field is not null;

    public byte[] EncryptControlField(ulong counter, ReadOnlySpan<byte> plaintext)
        => throw new NotSupportedException(NoFieldCrypto);

    public byte[] DecryptControlField(ulong counter, ReadOnlySpan<byte> ciphertext)
        => throw new NotSupportedException(NoFieldCrypto);

    /// <summary>
    /// Encrypt/decrypt the launchSpec config (AES-128-OFB), byte-identical to v1. v2 uses it at counter 0 (v1
    /// used 5, after its five field-crypt headers), over the v1 template minus <c>handshakeKey</c>.
    /// </summary>
    public byte[] CryptStreaminfo(ulong counter, ReadOnlySpan<byte> data)
        => (_field ?? throw new InvalidOperationException("launchSpec crypto not established (call EstablishControl first)."))
            .StreaminfoCrypt(counter, data);

    // ---- IHalyardSessionCrypto: stream key agreement (Phase 2) ----
    // Identical to v1: the stream ECDH + per-direction KDF (HalyardStreamKeySchedule), with X in the handshakeKey
    // role. v2 differs from v1 only in the per-packet window rule below, so these carry over unchanged.

    public byte[] GenerateEphemeralPublicKey(int protocolVersion)
    {
        _streamEcdh?.Dispose();
        var curve = HalyardStreamKeySchedule.CurveForVersion(protocolVersion);
        var (keyPair, publicKey) = HalyardStreamKeySchedule.GenerateKeyPair(curve);
        _streamEcdh = keyPair;
        return publicKey;
    }

    /// <summary><c>ecdhSignature = HMAC-SHA256(X, publicKey)</c> — X in the handshakeKey role (confirmed 003/004).</summary>
    public byte[] ComputeEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey)
        => HalyardStreamKeySchedule.ComputeEcdhSignature(handshakeKey, publicKey);

    public bool VerifyEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signature)
        => HalyardStreamKeySchedule.VerifyEcdhSignature(handshakeKey, publicKey, signature);

    /// <summary>
    /// Complete the stream key agreement. <paramref name="handshakeKey"/> is the per-session secret <c>X</c>
    /// (<see cref="HalyardV2SessionCrypto"/> class remarks): the caller supplies it; this class does not derive
    /// it. Per-direction keys follow the v1 schedule <c>HMAC(Z_stream, 01|dir|00|X|0100)</c>, dir 2 = c2s,
    /// dir 3 = s2c — the packet crypto then applies the v2 window rule.
    /// </summary>
    public bool TryEstablishStream(ReadOnlySpan<byte> peerPublicKey, ReadOnlySpan<byte> handshakeKey)
    {
        if (_streamEcdh is null)
            throw new InvalidOperationException("Call GenerateEphemeralPublicKey before establishing the stream.");

        byte[] sharedSecret;
        try
        {
            sharedSecret = HalyardStreamKeySchedule.DeriveSharedSecret(_streamEcdh, peerPublicKey);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return false;
        }

        var sendKeys = HalyardStreamKeySchedule.DeriveDirection(sharedSecret, handshakeKey, HalyardStreamKeySchedule.DirectionClientToServer);
        var recvKeys = HalyardStreamKeySchedule.DeriveDirection(sharedSecret, handshakeKey, HalyardStreamKeySchedule.DirectionServerToClient);
        _send = new HalyardPacketCrypto(sendKeys, StreamWindowRule);
        _receive = new HalyardPacketCrypto(recvKeys, StreamWindowRule);
        return true;
    }

    public bool IsStreamEstablished => _send is not null && _receive is not null;

    // ---- IHalyardSessionCrypto: per-packet stream crypto (Phase 2) ----

    /// <summary>Verify the 4-byte GMAC then AES-CTR-decrypt an incoming (s2c A/V) packet in place. The caller's
    /// <paramref name="layout"/> carries the v2 offsets (A/V tag@18, key_pos@22, payload@26).</summary>
    public bool TryOpenPacket(Span<byte> packet, in HalyardPacketLayout layout)
    {
        var receive = _receive;
        if (receive is null)
            return false; // stream not yet established — drop rather than throw
        if (!receive.VerifyPacket(layout.KeyPos, packet, layout.TagOffset))
            return false;

        receive.CryptPayloadInPlace(layout.KeyPos, packet[layout.PayloadOffset..]);
        return true;
    }

    /// <summary>AES-CTR encrypt an outgoing packet's payload in place, then write the 4-byte GMAC (encrypt-then-MAC).</summary>
    public void SealPacket(Span<byte> packet, in HalyardPacketLayout layout)
    {
        var send = _send ?? throw new InvalidOperationException("Stream crypto not established.");
        var encrypted = send.CryptPayload(layout.KeyPos, packet[layout.PayloadOffset..]);
        encrypted.CopyTo(packet[layout.PayloadOffset..]);

        var tag = send.ComputeTag(layout.KeyPos, packet, layout.TagOffset);
        tag.CopyTo(packet.Slice(layout.TagOffset, HalyardPacketCrypto.TagLength));
    }

    // ---- IHalyardSessionCrypto: c2s send-authentication (Phase 2) ----
    //
    // Each outgoing authenticated format is a v1 layout under the v1 client->console (dir-2) key held in _send,
    // with two v2 deltas: _send already rotates the GMAC key on the v2 window rule, and the per-format header
    // offsets shift. Confirmed byte-exact c2s over our captures 003/004 (V2-SPEC §; Fork H: 004 482/482,
    // 003 287/287). The three formats:
    //   * control DATA (base type 0x00): GMAC tag@5, key_pos@9; AAD zeroes tag+key_pos; payload NOT encrypted.
    //     Same offsets as v1 control — only the GMAC window rule differs.
    //   * congestion/RTT (base type 0x05): the v2/PS5 23-byte form (seq@1, 90 kHz ts@3), GMAC tag@15,
    //     key_pos@19; AAD zeroes tag+key_pos. v1/PS4 uses the 15-byte form at tag@7/key_pos@11 (V1-STREAM-AUDIT).
    //   * feedback/input (base type 0x06 state / 0x01 history): key_pos@12, GMAC tag@16, payload@20 (the caller's
    //     payloadOffset); AAD zeroes the tag only and the payload IS AES-CTR encrypted, the v1 feedback carrier
    //     shifted (v1 uses key_pos@4/tag@8/payload@12; c2s CTR nonce = v1).
    // All three share one advancing key position so no (key, nonce) ever repeats across formats or threads.

    private const int ControlTagOffset = 5;
    private const int ControlKeyPosOffset = 9;
    private const int ControlHeaderLength = 13; // tag[5..9) + key_pos[9..13)

    private const int CongestionTagOffset = 15;
    private const int CongestionKeyPosOffset = 19;
    private const int CongestionPacketLength = 23; // v2/PS5 23-byte congestion form

    private const int FeedbackKeyPosOffset = 12;
    private const int FeedbackTagOffset = 16;

    private ulong _sendKeyPos;                       // advancing local key position for all outgoing auth formats
    private readonly Lock _sendKeyPosGate = new();

    public void SealControlMessage(Span<byte> packet)
    {
        var send = _send;
        if (send is null || packet.Length < ControlHeaderLength)
            return; // pre-key-agreement (or malformed) — leave unauthenticated, correct before keys exist

        ulong keyPos = ReserveKeyPos(packet.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(ControlKeyPosOffset, 4), (uint)keyPos);
        byte[] tag = send.ComputeTag(keyPos, packet, ControlTagOffset, zeroKeyPos: true);
        tag.CopyTo(packet.Slice(ControlTagOffset, HalyardPacketCrypto.TagLength));
    }

    public void SealCongestionPacket(Span<byte> packet)
    {
        var send = _send;
        if (send is null || packet.Length < CongestionPacketLength)
            return; // pre-key-agreement — leave unauthenticated (never sent before keys exist anyway)

        ulong keyPos = ReserveKeyPos(packet.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(CongestionKeyPosOffset, 4), (uint)keyPos);
        byte[] tag = send.ComputeTag(keyPos, packet, CongestionTagOffset, zeroKeyPos: true);
        tag.CopyTo(packet.Slice(CongestionTagOffset, HalyardPacketCrypto.TagLength));
    }

    public void SealFeedbackPacket(Span<byte> packet, int payloadOffset)
    {
        var send = _send;
        if (send is null || payloadOffset < FeedbackTagOffset + HalyardPacketCrypto.TagLength || packet.Length < payloadOffset)
            return; // pre-key-agreement or too short — never sealed before keys exist

        ulong keyPos = ReserveKeyPos(packet.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(FeedbackKeyPosOffset, 4), (uint)keyPos);
        // Encrypt the payload in place, then MAC the whole (encrypted) packet with only the tag zeroed (like A/V).
        send.CryptPayloadInPlace(keyPos, packet[payloadOffset..]);
        byte[] tag = send.ComputeTag(keyPos, packet, FeedbackTagOffset, zeroKeyPos: false);
        tag.CopyTo(packet.Slice(FeedbackTagOffset, HalyardPacketCrypto.TagLength));
    }

    // Reserve a key position, advancing the shared counter by the packet's 16-byte-aligned length so distinct
    // formats sealed from different threads never collide on a (key, nonce) — the v1 rule.
    private ulong ReserveKeyPos(int packetLength)
    {
        int aligned = packetLength + (HalyardPacketCrypto.BlockSize - packetLength % HalyardPacketCrypto.BlockSize) % HalyardPacketCrypto.BlockSize;
        lock (_sendKeyPosGate)
        {
            ulong keyPos = _sendKeyPos;
            _sendKeyPos += (ulong)aligned;
            return keyPos;
        }
    }

    // ---- IHalyardV2ControlCrypto: the GCM envelope (Path A) ----

    public byte[] GenerateControlPublicKey()
    {
        _controlEcdh?.Dispose();
        // The control ECDH is its own P-521 key pair, separate from the stream plane's.
        var (keyPair, publicKey) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        _controlEcdh = keyPair;
        return publicKey;
    }

    public byte[] ComputeRpHmac(ReadOnlySpan<byte> k16, ReadOnlySpan<byte> publicKey)
    {
        // RP-Hmac is over the first 133 bytes (the uncompressed P-521 point); the base64 RP-Pubkey decodes to 135.
        const int point = HalyardStreamKeySchedule.PublicKeyLengthP521;
        if (publicKey.Length < point)
            throw new ArgumentException($"Public key must be at least {point} bytes.", nameof(publicKey));
        return HMACSHA256.HashData(k16, publicKey[..point]);
    }

    public bool EstablishControl(ReadOnlySpan<byte> consolePublicKey, ReadOnlySpan<byte> k16)
    {
        if (_controlEcdh is null)
            throw new InvalidOperationException("Call GenerateControlPublicKey before establishing the control plane.");
        if (k16.Length != 16)
            return false;

        byte[] z;
        try
        {
            z = HalyardStreamKeySchedule.DeriveSharedSecret(_controlEcdh, consolePublicKey);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return false;
        }

        // sk_p = HMAC-SHA256(Z, 01|p|00|K16|0100) — the v1 DeriveDirection form with K16 in the handshakeKey role.
        _skClientToConsole = HalyardStreamKeySchedule.DeriveDirection(z, k16, PurposeClientToConsole);
        _skConsoleToClient = HalyardStreamKeySchedule.DeriveDirection(z, k16, PurposeConsoleToClient);
        return true;
    }

    bool IHalyardV2ControlCrypto.IsControlEstablished => _skClientToConsole is not null;

    public byte[] SealClientToConsole(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, ulong counter)
    {
        var sk = _skClientToConsole ?? throw new InvalidOperationException("Control plane not established.");
        byte[] iv = DeriveControlIv(sk, counter);
        var (ciphertext, tag) = AesGcmCore.Encrypt(sk.AesKey, iv, aad, plaintext);

        byte[] result = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(result, 0);
        tag.CopyTo(result, ciphertext.Length);
        return result;
    }

    public byte[]? OpenConsoleToClient(ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad, ulong counter)
    {
        var sk = _skConsoleToClient ?? throw new InvalidOperationException("Control plane not established.");
        if (ciphertextAndTag.Length < GcmTagLength)
            return null;

        ReadOnlySpan<byte> ciphertext = ciphertextAndTag[..^GcmTagLength];
        ReadOnlySpan<byte> tag = ciphertextAndTag[^GcmTagLength..];
        byte[] iv = DeriveControlIv(sk, counter);
        return AesGcmCore.Decrypt(sk.AesKey, iv, aad, ciphertext, tag);
    }

    // IV = HMAC-SHA256(N16, sk_p[16:32] ‖ be64(counter))[0:16]. N16 is the FIXED selectorOne context key from the
    // bundle (confirmed against captures 004/009 via validate_path_a.py — NOT the per-session RP-Nonce, which is
    // used only for the launchSpec M). BaseIv is sk_p[16:32]. The counter restarts per TCP connection (0 for
    // init2 and the ctrl request, each on its own connection), so those reuse key+IV — the caller passes it.
    private byte[] DeriveControlIv(in HalyardStreamKeySchedule.DirectionKeys sk, ulong counter)
    {
        Span<byte> data = stackalloc byte[HalyardStreamKeySchedule.BaseIvLength + sizeof(ulong)];
        sk.BaseIv.CopyTo(data);
        BinaryPrimitives.WriteUInt64BigEndian(data[HalyardStreamKeySchedule.BaseIvLength..], counter);

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_secrets.ContextKeys.SelectorOne.Span, data, mac);
        return mac[..16].ToArray();
    }

    public void Dispose()
    {
        _controlEcdh?.Dispose();
        _controlEcdh = null;
        _streamEcdh?.Dispose();
        _streamEcdh = null;
        _send?.Dispose();
        _send = null;
        _receive?.Dispose();
        _receive = null;
    }
}
