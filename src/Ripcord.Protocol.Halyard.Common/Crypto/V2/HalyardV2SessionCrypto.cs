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
///   <item><description><b>Per-packet</b> — v2 shifts the A/V header offsets (tag@18, keystream_offset@22, the caller's
///   <see cref="HalyardPacketLayout"/>); the GMAC key rotation
///   (<see cref="HalyardPacketCrypto.WindowOf"/>) and AES-CTR's little-endian block counter are v1's
///   unchanged. So the per-packet methods reuse <see cref="HalyardPacketCrypto"/>
///   (Phase 2, implemented here for the s2c A/V receive path + key agreement).</description></item>
/// </list>
///
/// <para><b>How <c>X</c> is produced — UNSOLVED (<c>[X]</c>), and the earlier "SOLVED = launchSpec handshakeKey"
/// answer was WRONG.</b> Hardware disproved it (2026-10-22): on a live PS5 that reached SESSION_REPLY, the
/// console's stream-pubkey signature is <b>not</b> <c>HMAC(launchSpecHandshakeKey, pubkey)</c> — nor HMAC of the
/// RP-VerifyKey, K16, companion, sk8/sk9, or any derivation of the control ECDH secret Z we tried (~1400
/// candidates). So the stream key is <b>console-side / derived, never the client-random launchSpec value</b>.
/// Our own 8.5 captures + our own binary analysis (cross-referenced against our own 9.5 RE) point to the real
/// mechanism: the stream uses a <b>single HMAC key bound to the control-plane secret Z</b> for BOTH the
/// console-pubkey authentication (what we call the ecdhSignature) AND the per-direction stream KDF (with the
/// stream ECDH secret <c>Z_stream</c> as the KDF message). That means the <c>handshakeKey</c>/<c>X</c> role here
/// is mismodelled and the key/secret arguments to the stream KDF are likely swapped. The exact derivation of that
/// HMAC key from Z is the open <c>[X]</c> — to pin by RE of our own 8.5 binary ("ECDH hmac key generation") and
/// confirm on our own 8.5 wire. Until then the v2 stream plane does not establish against a real console.</para>
///
/// <para>The c2s send-authentication methods (<c>SealControlMessage</c>/<c>SealLossReport</c>/
/// <c>SealInputPacket</c>) are implemented (Phase 2): each is a v1 layout under the v1 client->console key,
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
    // packet crypto.
    private ECDiffieHellman? _streamEcdh;
    private HalyardPacketCrypto? _send;    // client -> console (input)
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

    /// <summary><c>ecdhSignature = HMAC-SHA256(key, publicKey)</c>. The structure (HMAC over the pubkey) matched
    /// our captured 003/004 vectors, but the <b>key</b> is <c>[X]</c>: on hardware the console does NOT sign with
    /// the launchSpec <c>handshakeKey</c> this takes — see the class remarks. Verifying with that key fails against
    /// a real console, so this call is correct in shape only until the real key (bound to Z) is derived.</summary>
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
        _send = new HalyardPacketCrypto(sendKeys);
        _receive = new HalyardPacketCrypto(recvKeys);
        return true;
    }

    public bool IsStreamEstablished => _send is not null && _receive is not null;

    // ---- IHalyardSessionCrypto: per-packet stream crypto (Phase 2) ----

    /// <summary>Verify the 4-byte GMAC then AES-CTR-decrypt an incoming (s2c A/V) packet in place. The caller's
    /// <paramref name="layout"/> carries the v2 offsets (A/V tag@18, keystream_offset@22, payload@26).</summary>
    public bool TryOpenPacket(Span<byte> packet, in HalyardPacketLayout layout)
    {
        var receive = _receive;
        if (receive is null)
            return false; // stream not yet established — drop rather than throw
        if (!receive.VerifyPacket(layout.KeystreamOffset, packet, layout.TagOffset))
            return false;

        receive.CryptPayloadInPlace(layout.KeystreamOffset, packet[layout.PayloadOffset..]);
        return true;
    }

    /// <summary>AES-CTR encrypt an outgoing packet's payload in place, then write the 4-byte GMAC (encrypt-then-MAC).</summary>
    public void SealPacket(Span<byte> packet, in HalyardPacketLayout layout)
    {
        var send = _send ?? throw new InvalidOperationException("Stream crypto not established.");
        var encrypted = send.CryptPayload(layout.KeystreamOffset, packet[layout.PayloadOffset..]);
        encrypted.CopyTo(packet[layout.PayloadOffset..]);

        var tag = send.ComputeTag(layout.KeystreamOffset, packet, layout.TagOffset);
        tag.CopyTo(packet.Slice(layout.TagOffset, HalyardPacketCrypto.TagLength));
    }

    // ---- IHalyardSessionCrypto: c2s send-authentication (Phase 2) ----
    //
    // Each outgoing authenticated format is a v1 layout under the v1 client->console (dir-2) key held in _send,
    // with two v2 deltas: _send already rotates the GMAC key on the v2 window rule, and the per-format header
    // offsets shift. Confirmed byte-exact c2s over our captures 003/004 (V2-SPEC §; Fork H: 004 482/482,
    // 003 287/287). The three formats:
    //   * control DATA (base type 0x00): GMAC tag@5, keystream_offset@9; AAD zeroes tag+keystream_offset; payload NOT encrypted.
    //     Same offsets as v1 control — only the GMAC window rule differs.
    //   * loss report/RTT (base type 0x05): the v2/PS5 23-byte form (seq@1, 90 kHz ts@3), GMAC tag@15,
    //     keystream_offset@19; AAD zeroes tag+keystream_offset. v1/PS4 uses the 15-byte form at tag@7/keystream_offset@11 (V1-STREAM-AUDIT).
    //   * input (base type 0x06 state / 0x01 history): keystream_offset@12, GMAC tag@16, payload@20 (the caller's
    //     payloadOffset); AAD zeroes the tag only and the payload IS AES-CTR encrypted, the v1 input carrier
    //     shifted (v1 uses keystream_offset@4/tag@8/payload@12; c2s CTR nonce = v1).
    // All three share one advancing keystream offset so no (key, nonce) ever repeats across formats or threads.

    private const int ControlTagOffset = 5;
    private const int ControlKeystreamOffsetAt = 9;
    private const int ControlHeaderLength = 13; // tag[5..9) + keystream_offset[9..13)

    private const int LossReportTagOffset = 15;
    private const int LossReportKeystreamOffsetAt = 19;
    private const int LossReportLength = 23; // v2/PS5 23-byte loss-report form

    private const int InputKeystreamOffsetAt = 12;
    private const int InputTagOffset = 16;

    private ulong _sendKeystreamOffset;                       // advancing local keystream offset for all outgoing auth formats
    private readonly Lock _sendKeystreamOffsetGate = new();

    public void SealControlMessage(Span<byte> packet)
    {
        var send = _send;
        if (send is null || packet.Length < ControlHeaderLength)
            return; // pre-key-agreement (or malformed) — leave unauthenticated, correct before keys exist

        ulong keystreamOffset = ReserveKeystreamOffset(packet.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(ControlKeystreamOffsetAt, 4), (uint)keystreamOffset);
        byte[] tag = send.ComputeTag(keystreamOffset, packet, ControlTagOffset, zeroKeystreamOffset: true);
        tag.CopyTo(packet.Slice(ControlTagOffset, HalyardPacketCrypto.TagLength));
    }

    public void SealLossReport(Span<byte> packet)
    {
        var send = _send;
        if (send is null || packet.Length < LossReportLength)
            return; // pre-key-agreement — leave unauthenticated (never sent before keys exist anyway)

        ulong keystreamOffset = ReserveKeystreamOffset(packet.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(LossReportKeystreamOffsetAt, 4), (uint)keystreamOffset);
        byte[] tag = send.ComputeTag(keystreamOffset, packet, LossReportTagOffset, zeroKeystreamOffset: true);
        tag.CopyTo(packet.Slice(LossReportTagOffset, HalyardPacketCrypto.TagLength));
    }

    public void SealInputPacket(Span<byte> packet, int payloadOffset)
    {
        var send = _send;
        if (send is null || payloadOffset < InputTagOffset + HalyardPacketCrypto.TagLength || packet.Length < payloadOffset)
            return; // pre-key-agreement or too short — never sealed before keys exist

        ulong keystreamOffset = ReserveKeystreamOffset(packet.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(InputKeystreamOffsetAt, 4), (uint)keystreamOffset);
        // Encrypt the payload in place, then MAC the whole (encrypted) packet with only the tag zeroed (like A/V).
        send.CryptPayloadInPlace(keystreamOffset, packet[payloadOffset..]);
        byte[] tag = send.ComputeTag(keystreamOffset, packet, InputTagOffset, zeroKeystreamOffset: false);
        tag.CopyTo(packet.Slice(InputTagOffset, HalyardPacketCrypto.TagLength));
    }

    // Reserve a keystream offset, advancing the shared counter by the packet's 16-byte-aligned length so distinct
    // formats sealed from different threads never collide on a (key, nonce) — the v1 rule.
    private ulong ReserveKeystreamOffset(int packetLength)
    {
        int aligned = packetLength + (HalyardPacketCrypto.BlockSize - packetLength % HalyardPacketCrypto.BlockSize) % HalyardPacketCrypto.BlockSize;
        lock (_sendKeystreamOffsetGate)
        {
            ulong keystreamOffset = _sendKeystreamOffset;
            _sendKeystreamOffset += (ulong)aligned;
            return keystreamOffset;
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
