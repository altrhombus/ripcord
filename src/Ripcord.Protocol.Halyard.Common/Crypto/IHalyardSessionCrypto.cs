namespace Ripcord.Protocol.Halyard.Common.Crypto;

/// <summary>
/// THE SEAM. The v1 session crypto has two independent systems (spec §2, §5):
/// <list type="bullet">
///   <item><description><b>Control plane</b> — a symmetric key derived from the RP-Nonce + companion,
///   encrypting a handful of <c>/sess/ctrl</c> HTTP header fields and the streaminfo config.</description></item>
///   <item><description><b>Stream plane</b> — an ephemeral ECDH exchange (authenticated by the streaminfo
///   <c>handshakeKey</c>) feeding per-direction, per-packet AES-CTR encryption + GMAC.</description></item>
/// </list>
///
/// One instance per session, stateful: it holds the control key after <see cref="EstablishControl"/>, the
/// ephemeral private key between <see cref="GenerateEphemeralPublicKey"/> and
/// <see cref="TryEstablishStream"/>, and the per-direction packet keys after that.
///
/// Everything else in the connect path is built and tested against
/// <see cref="PassthroughHalyardSessionCrypto"/>. The real implementation is derived from our own analysis of
/// the vendor binary and our own captures (see docs/protocol/). A small number of values are still
/// <b>assumptions</b> rather than confirmed findings; those are tagged <b>[X]</b> in the spec and listed in
/// the roadmap. An <b>[X]</b> value is provisional — don't treat it as settled.
/// </summary>
public interface IHalyardSessionCrypto
{
    // ---- control plane (spec §2) ----

    /// <summary>Derive and store the control-session key from the handshake material.</summary>
    void EstablishControl(in HalyardControlKeyMaterial material);

    bool IsControlEstablished { get; }

    /// <summary>Encrypt one <c>/sess/ctrl</c> field (AES-128-CFB) at its field counter; returns raw ciphertext.</summary>
    byte[] EncryptControlField(ulong counter, ReadOnlySpan<byte> plaintext);

    /// <summary>Decrypt one <c>/sess/ctrl</c> field (AES-128-CFB) at its field counter.</summary>
    byte[] DecryptControlField(ulong counter, ReadOnlySpan<byte> ciphertext);

    /// <summary>Encrypt/decrypt the streaminfo/launchSpec config (AES-128-OFB, symmetric).</summary>
    byte[] CryptStreaminfo(ulong counter, ReadOnlySpan<byte> data);

    // ---- stream key agreement (spec §5.1-5.4) ----

    /// <summary>
    /// Generate the ephemeral ECDH public key for this session, retaining the private key internally for
    /// <see cref="TryEstablishStream"/>. The curve follows the negotiated <paramref name="protocolVersion"/>
    /// (versions 0x0d–0x11 use P-521 → a 133-byte point; others P-256 → 65 bytes).
    /// </summary>
    byte[] GenerateEphemeralPublicKey(int protocolVersion);

    /// <summary>The <c>ecdhSignature</c> for a public key: HMAC-SHA256(handshakeKey, publicKey).</summary>
    byte[] ComputeEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey);

    /// <summary>Verify a peer's <c>ecdhSignature</c> over its public key (constant time).</summary>
    bool VerifyEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signature);

    /// <summary>
    /// Complete the stream key agreement: ECDH with the peer's public key, then derive the per-direction
    /// packet keys from the shared secret and <paramref name="handshakeKey"/>. Returns false if the inputs
    /// are unusable.
    /// </summary>
    bool TryEstablishStream(ReadOnlySpan<byte> peerPublicKey, ReadOnlySpan<byte> handshakeKey);

    bool IsStreamEstablished { get; }

    // ---- per-packet stream crypto (spec §5.5) ----

    /// <summary>
    /// Verify the 4-byte GMAC over an incoming (down-direction) packet at its key position, then AES-CTR
    /// decrypt the payload in place. Returns false if authentication fails (the packet is left untouched
    /// on failure). Operates on the whole packet; <paramref name="layout"/> locates the fields.
    /// </summary>
    bool TryOpenPacket(Span<byte> packet, in HalyardPacketLayout layout);

    /// <summary>
    /// AES-CTR encrypt an outgoing (up-direction) packet's payload in place, then compute and write the
    /// 4-byte GMAC tag (encrypt-then-MAC).
    /// </summary>
    void SealPacket(Span<byte> packet, in HalyardPacketLayout layout);

    /// <summary>
    /// Authenticate an outgoing <em>control</em> DATA packet in place, once the stream keys are established:
    /// write an advancing key position into the header and the 4-byte GMAC tag over the whole packet. Unlike
    /// <see cref="SealPacket"/> the payload is NOT encrypted (control protobufs stay cleartext) — it is
    /// MAC-only. A no-op before the stream is established (pre-key-agreement DATA goes out unauthenticated,
    /// which is correct). The console drops unauthenticated control once keys are up (wire-confirmed).
    /// </summary>
    void SealControlMessage(Span<byte> packet);

    /// <summary>
    /// Authenticate an outgoing congestion-feedback packet (base type 5) in place: write the advancing key
    /// position (offset 0xb) and the 4-byte GMAC tag (offset 7) over the whole packet. Like control, the GMAC
    /// zeroes both the tag and the key-position field; the packet has no encrypted payload. Shares the same
    /// outgoing key-position counter as <see cref="SealControlMessage"/> so nonces never repeat. No-op before
    /// the stream keys are established.
    /// </summary>
    void SealCongestionPacket(Span<byte> packet);

    /// <summary>
    /// Seal an outgoing controller-feedback packet (base type 6 = state, 1 = history) in place: reserve an
    /// advancing key position from the shared outgoing counter, write it (offset 4), AES-CTR encrypt the
    /// payload (from <paramref name="payloadOffset"/>, normally 0xc), then compute + write the 4-byte GMAC
    /// tag (offset 8) over the whole packet. Like A/V — and unlike control/congestion — the GMAC zeroes only
    /// the tag, not the key-position field. Shares the same key-position counter as control/congestion so
    /// nonces never repeat across the up direction. No-op before the stream keys are established.
    /// </summary>
    void SealFeedbackPacket(Span<byte> packet, int payloadOffset);
}

/// <summary>First-time registration seam (obtaining a registration key). Stubbed until registration is derived.</summary>
public interface IHalyardRegistration
{
    /// <summary>
    /// Get whatever this route needs ready before the caller has finished negotiating. A no-op for the PIN
    /// route; the account route opens its control association here, because the side that opens it is the side
    /// that may open a connection on it and the console races us for that the moment our ACCEPT lands.
    /// </summary>
    Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task<HalyardRegistrationResult> RegisterAsync(HalyardRegistrationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The registration body cipher (spec §2.0, confidence <b>[V]</b> — reversed and validated end-to-end).
/// Registration is a PIN-authenticated key exchange: the client puts a fresh random <em>context</em> at the
/// head of the request body, and a single transport key <c>K</c> protects both directions —
/// <c>K = registrationTable[context[selectorOffset] &amp; 0x1f]</c> with the passcode folded (big-endian) into
/// its last 4 bytes. Both the request field and the response then use the §2.1 field cipher (AES-128-CFB,
/// context key <c>B_eq_1</c>, counter 0) with one shared <c>material</c>.
///
/// <para>The transform is implemented in <see cref="V1.HalyardRegistrationKdf"/> /
/// <see cref="V1.HalyardRegistrationCipher"/>; the lookup table + selector offset are extracted interop
/// constants supplied from the gitignored dirty room (never committed), so the concrete cipher is injected by
/// config. The default is <see cref="UnavailableRegistrationCipher"/>.</para>
///
/// <para>Callers pass the request <paramref name="requestContext"/> (the body's leading random bytes, which
/// both peers hold) and the per-pairing <paramref name="material"/>. Deriving these for an <em>outbound</em>
/// request is the remaining send-side work (the context byte-layout the console gathers <c>material</c> from);
/// the inbound path (decrypting a response to the pairing record) is complete.</para>
/// </summary>
public interface IHalyardRegistrationCipher
{
    /// <summary>Whether the real transform is available (a dirty-room implementation is injected).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Build a registration request body for the given <paramref name="passcode"/> and field plaintext
    /// (<c>Client-Type</c>/<c>Np-AccountId</c>): generate a random context + material, derive the key,
    /// scatter the wrapped material so the console can recover it, and encrypt the field. Returns the
    /// body to POST plus the state needed to decrypt the response.
    /// </summary>
    HalyardRegistrationExchange BuildRequest(string passcode, ReadOnlySpan<byte> fieldPlaintext);

    /// <summary>
    /// Build an account ("web"/no-PIN) registration request. Like <see cref="BuildRequest"/> but the transport
    /// key is <c>seed XOR registrationTable[selector]</c> — the 16-byte <paramref name="seed"/> is the
    /// console-delivered value recovered from <c>customData1</c> (see
    /// <see cref="Crypto.V1.HalyardAccountSeedDelivery"/>), not an on-console passcode.
    /// </summary>
    HalyardRegistrationExchange BuildAccountRequest(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> fieldPlaintext);

    /// <summary>Decrypt the console's response body into the raw pairing-record bytes for a given exchange.</summary>
    byte[] DecryptResponse(HalyardRegistrationExchange exchange, ReadOnlySpan<byte> responseBody);
}

/// <summary>
/// The in-flight state of one registration exchange: the request body to send, plus the (context, material,
/// passcode) the response decrypt needs. The material is the per-pairing value, carried in the request in
/// wrapped form.
/// </summary>
public sealed class HalyardRegistrationExchange
{
    public required byte[] RequestBody { get; init; }
    internal byte[] Context { get; init; } = [];
    internal byte[] Material { get; init; } = [];
    internal string Passcode { get; init; } = "";

    /// <summary>The account ("web"/no-PIN) route's 16-byte registration seed, when set; empty for the PIN
    /// route. When present, the transport key is <c>seed XOR registrationTable[selector]</c> rather than the
    /// passcode-folded entry. See <see cref="Crypto.V1.HalyardAccountSeedDelivery"/>.</summary>
    internal byte[] Seed { get; init; } = [];
}

/// <summary>Default registration cipher: reports unavailable so registration fails cleanly until the real
/// (dirty-room) transform is injected. Keeps the DI graph and pairing UI buildable without any secret.</summary>
public sealed class UnavailableRegistrationCipher : IHalyardRegistrationCipher
{
    public bool IsAvailable => false;

    public HalyardRegistrationExchange BuildRequest(string passcode, ReadOnlySpan<byte> fieldPlaintext)
        => throw new NotSupportedException("Registration cipher is not available (the dirty-room tables are not injected).");

    public HalyardRegistrationExchange BuildAccountRequest(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> fieldPlaintext)
        => throw new NotSupportedException("Registration cipher is not available (the dirty-room tables are not injected).");

    public byte[] DecryptResponse(HalyardRegistrationExchange exchange, ReadOnlySpan<byte> responseBody)
        => throw new NotSupportedException("Registration cipher is not available (the dirty-room tables are not injected).");
}
