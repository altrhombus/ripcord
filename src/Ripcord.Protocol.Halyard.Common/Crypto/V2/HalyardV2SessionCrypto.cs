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

    public HalyardV2SessionCrypto(HalyardControlSecrets secrets)
        => _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

    // ---- IHalyardSessionCrypto: control plane ----

    // v2 does NOT field-encrypt the control plane; it uses the GCM envelope on IHalyardV2ControlCrypto. These
    // are a deliberate not-supported, distinct from the phased "not implemented yet" below.
    private const string NoFieldCrypto =
        "v2 has no per-field control crypto; use the IHalyardV2ControlCrypto GCM envelope.";

    /// <summary>Establishes the control KDF (M) used for the launchSpec OFB — the v1 derivation, reused by v2.</summary>
    public void EstablishControl(in HalyardControlKeyMaterial material) => throw new NotImplementedException(NotYet);

    bool IHalyardSessionCrypto.IsControlEstablished => false;

    public byte[] EncryptControlField(ulong counter, ReadOnlySpan<byte> plaintext)
        => throw new NotSupportedException(NoFieldCrypto);

    public byte[] DecryptControlField(ulong counter, ReadOnlySpan<byte> ciphertext)
        => throw new NotSupportedException(NoFieldCrypto);

    public byte[] CryptStreaminfo(ulong counter, ReadOnlySpan<byte> data) => throw new NotImplementedException(NotYet);

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

    // ---- IHalyardV2ControlCrypto: the GCM envelope (Path A) (Phase 1 step 2) ----

    public byte[] GenerateControlPublicKey() => throw new NotImplementedException(NotYet);

    public byte[] ComputeRpHmac(ReadOnlySpan<byte> k16, ReadOnlySpan<byte> publicKey)
        => throw new NotImplementedException(NotYet);

    public bool EstablishControl(ReadOnlySpan<byte> consolePublicKey, ReadOnlySpan<byte> consoleNonce, ReadOnlySpan<byte> k16)
        => throw new NotImplementedException(NotYet);

    bool IHalyardV2ControlCrypto.IsControlEstablished => false;

    public byte[] SealClientToConsole(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, ulong counter)
        => throw new NotImplementedException(NotYet);

    public byte[]? OpenConsoleToClient(ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad, ulong counter)
        => throw new NotImplementedException(NotYet);

    public void Dispose()
    {
        // Nothing to dispose yet; the ECDH key pairs and packet ciphers arrive with their phases.
    }
}
