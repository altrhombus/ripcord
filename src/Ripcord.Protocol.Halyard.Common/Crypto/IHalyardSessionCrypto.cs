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
/// The connect path runs against any implementation; <see cref="PassthroughHalyardSessionCrypto"/> is the
/// identity one, for captures and tests. The real implementation is derived from our own analysis of
/// the vendor binary and our own captures (see docs/protocol/). Values that are <b>assumptions</b> rather
/// than confirmed findings are tagged <b>[X]</b> where they sit and in the spec. An <b>[X]</b> value is
/// provisional — don't treat it as settled.
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
    /// Verify the 4-byte GMAC over an incoming (down-direction) packet at its keystream offset, then AES-CTR
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
    /// write an advancing keystream offset into the header and the 4-byte GMAC tag over the whole packet. Unlike
    /// <see cref="SealPacket"/> the payload is NOT encrypted (control protobufs stay cleartext) — it is
    /// MAC-only. A no-op before the stream is established (pre-key-agreement DATA goes out unauthenticated,
    /// which is correct). The console drops unauthenticated control once keys are up (wire-confirmed).
    /// </summary>
    void SealControlMessage(Span<byte> packet);

    /// <summary>
    /// Authenticate an outgoing loss report (base type 5) in place: write the advancing key
    /// position (v1: offset 0xb; v2: 19) and the 4-byte GMAC tag (v1: offset 7; v2: 15) over the whole packet. Like control, the GMAC
    /// zeroes both the tag and the keystream-offset field; the packet has no encrypted payload. Shares the same
    /// outgoing keystream-offset counter as <see cref="SealControlMessage"/> so nonces never repeat. No-op before
    /// the stream keys are established.
    /// </summary>
    void SealLossReport(Span<byte> packet);

    /// <summary>
    /// Seal an outgoing controller-input packet (base type 6 = state, 1 = history) in place: reserve an
    /// advancing keystream offset from the shared outgoing counter, write it (v1: offset 4; v2: 12), AES-CTR encrypt the
    /// payload (from <paramref name="payloadOffset"/>; v1: 0xc, v2: 20), then compute + write the 4-byte GMAC
    /// tag (v1: offset 8; v2: 16) over the whole packet. Like A/V — and unlike control and loss reports — the GMAC zeroes only
    /// the tag, not the keystream-offset field. Shares the same keystream-offset counter as control and loss reports so
    /// nonces never repeat across the up direction. No-op before the stream keys are established.
    /// </summary>
    void SealInputPacket(Span<byte> packet, int payloadOffset);
}

/// <summary>First-time registration seam (obtaining a registration key).</summary>
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
/// constants that reach it as a <see cref="V1.HalyardRegistrationSecrets"/> (that type's remarks say where
/// they are loaded from), so the concrete cipher is built by whoever resolves those constants. With none
/// resolved, the cipher is <see cref="UnavailableRegistrationCipher"/>.</para>
///
/// <para>The request's random context and the per-pairing material are generated inside
/// <see cref="BuildRequest"/> / <see cref="BuildAccountRequest"/> and travel in the returned
/// <see cref="HalyardRegistrationExchange"/>, which <see cref="DecryptResponse"/> takes back.</para>
/// </summary>
public interface IHalyardRegistrationCipher
{
    /// <summary>Whether the real transform is available (false on <see cref="UnavailableRegistrationCipher"/>).</summary>
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

/// <summary>The registration cipher used when no interop constants resolve (for example a build made with
/// <c>-p:BundleInteropConstants=false</c> and no local fixture): reports unavailable and throws from every
/// operation, so registration fails cleanly and the pairing flow still constructs.</summary>
public sealed class UnavailableRegistrationCipher : IHalyardRegistrationCipher
{
    public bool IsAvailable => false;

    public HalyardRegistrationExchange BuildRequest(string passcode, ReadOnlySpan<byte> fieldPlaintext)
        => throw new NotSupportedException("Registration cipher is not available (the captures-folder tables are not injected).");

    public HalyardRegistrationExchange BuildAccountRequest(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> fieldPlaintext)
        => throw new NotSupportedException("Registration cipher is not available (the captures-folder tables are not injected).");

    public byte[] DecryptResponse(HalyardRegistrationExchange exchange, ReadOnlySpan<byte> responseBody)
        => throw new NotSupportedException("Registration cipher is not available (the captures-folder tables are not injected).");
}
