using System.Buffers.Binary;
using System.Security.Cryptography;
using Ripcord.Core.Net.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V2;

/// <summary>
/// The v2 (PS5, 8.5-class) session crypto. A <b>seam-and-stub</b> at present (CLAUDE.md, "Prefer this
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
///   <item><description><b>Stream key agreement</b> — the v1 schedule with the client-minted <c>X</c> in the
///   <c>handshakeKey</c> role (<see cref="HalyardStreamKeySchedule"/>), so the agreement methods carry over
///   (Phase 2).</description></item>
///   <item><description><b>Per-packet</b> — v2 shifts the header offsets (A/V tag@18, key_pos@22), rotates the
///   GMAC key with a <em>double</em> fold over the window-0 key, and increments the AES-CTR block counter
///   <b>little-endian</b> — so the per-packet methods need a v2 packet-crypto variant (Phase 2).</description></item>
/// </list>
/// </summary>
public sealed class HalyardV2SessionCrypto : IHalyardSessionCrypto, IHalyardV2ControlCrypto, IDisposable
{
    private const string NotYet = "HalyardV2SessionCrypto is a seam-and-stub; this path is not implemented yet.";

    // Held for the launchSpec KDF (M) and the per-direction stream schedule, exactly as the v1 crypto holds it.
    private readonly HalyardControlSecrets _secrets;
    private readonly HalyardControlKdf _kdf;

    // The launchSpec cipher is v1's unchanged: AES-128-OFB under M = KDF(RP-Nonce, RP-Key), IV keyed by the
    // selectorOne context key (confirmed: the v2 launchSpec differs from v1 only by counter 0 and dropping
    // handshakeKey, LAUNCHSPEC-CTRL-FIELDS §1), so v2 reuses the v1 field crypto rather than re-deriving it.
    private HalyardControlFieldCrypto? _field;

    // Path A (control plane). The purposes are the info-string's second byte: 0x09 client->console, 0x08 the
    // other way (spec §4). The GCM tag is the full 16 bytes.
    private const byte PurposeClientToConsole = 0x09;
    private const byte PurposeConsoleToClient = 0x08;
    private const int GcmTagLength = 16;

    private ECDiffieHellman? _controlEcdh;
    private HalyardStreamKeySchedule.DirectionKeys? _skClientToConsole; // sk_9: AesKey = sk[0:16], BaseIv = sk[16:32]
    private HalyardStreamKeySchedule.DirectionKeys? _skConsoleToClient; // sk_8

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

    public byte[] GenerateEphemeralPublicKey(int protocolVersion) => throw new NotImplementedException(NotYet);

    public byte[] ComputeEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey)
        => throw new NotImplementedException(NotYet);

    public bool VerifyEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signature)
        => throw new NotImplementedException(NotYet);

    public bool TryEstablishStream(ReadOnlySpan<byte> peerPublicKey, ReadOnlySpan<byte> handshakeKey)
        => throw new NotImplementedException(NotYet);

    public bool IsStreamEstablished => false;

    // ---- IHalyardSessionCrypto: per-packet stream crypto (Phase 2) ----

    public bool TryOpenPacket(Span<byte> packet, in HalyardPacketLayout layout) => throw new NotImplementedException(NotYet);
    public void SealPacket(Span<byte> packet, in HalyardPacketLayout layout) => throw new NotImplementedException(NotYet);
    public void SealControlMessage(Span<byte> packet) => throw new NotImplementedException(NotYet);
    public void SealCongestionPacket(Span<byte> packet) => throw new NotImplementedException(NotYet);
    public void SealFeedbackPacket(Span<byte> packet, int payloadOffset) => throw new NotImplementedException(NotYet);

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
    }
}
