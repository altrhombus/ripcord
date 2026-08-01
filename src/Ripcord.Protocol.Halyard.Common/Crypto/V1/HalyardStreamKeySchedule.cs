using System.Security.Cryptography;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V1;

/// <summary>The ECDH curve for the stream key agreement, selected by the negotiated protocol version.</summary>
public enum HalyardStreamCurve
{
    NistP256, // 65-byte pubkey, 32-byte shared X
    NistP521, // 133-byte pubkey, 66-byte shared X
}

/// <summary>
/// The v1 stream key agreement. Each side generates an ephemeral ECDH keypair and exchanges the uncompressed
/// public key, authenticated by <c>ecdhSignature = HMAC-SHA256(handshakeKey, pubkey)</c> (handshakeKey being
/// the 16 random bytes carried in the streaminfo). After each side verifies the peer's signature, the shared
/// secret is the ECDH X coordinate, from which per-direction AES-128 key/IV pairs are derived with an
/// SP 800-108-style single-block HMAC-SHA256 KDF.
///
/// <para>The curve is <b>version-dependent</b> (wire-confirmed): protocol versions 0x0d–0x11 use <b>P-521</b>
/// (133-byte pubkey), others use P-256 (65-byte). The shared X (32 bytes for P-256, 66 for P-521) is used
/// directly as the HMAC key with <b>no reduction</b> - live-validated against a full 66-byte P-521 X dump,
/// which reproduces the real console's per-direction keys byte-for-byte (HMAC-SHA256 internally hashes the
/// over-length key).</para>
///
/// <para>Directions: client→server = 2, server→client = 3 (from the client's point of view).</para>
/// </summary>
public static class HalyardStreamKeySchedule
{
    public const int PublicKeyLength = 65; // P-256: 0x04 || X(32) || Y(32)
    public const int PublicKeyLengthP521 = 133; // 0x04 || X(66) || Y(66)
    public const int AesKeyLength = 16;
    public const int BaseIvLength = 16;

    public const byte DirectionClientToServer = 2;
    public const byte DirectionServerToClient = 3;

    /// <summary>Map a negotiated protocol version to its ECDH curve (0x0d–0x11 → P-521).</summary>
    public static HalyardStreamCurve CurveForVersion(int protocolVersion)
        => protocolVersion is >= 0x0d and <= 0x11 ? HalyardStreamCurve.NistP521 : HalyardStreamCurve.NistP256;

    private static ECCurve ToEcCurve(HalyardStreamCurve c)
        => c == HalyardStreamCurve.NistP521 ? ECCurve.NamedCurves.nistP521 : ECCurve.NamedCurves.nistP256;

    private static int CoordinateSize(HalyardStreamCurve c) => c == HalyardStreamCurve.NistP521 ? 66 : 32;

    /// <summary>A per-direction key/IV pair derived from the shared secret and handshakeKey.</summary>
    public readonly record struct DirectionKeys(byte[] AesKey, byte[] BaseIv);

    /// <summary>The <c>ecdhSignature</c> that accompanies a public key in the SESSION_REQUEST/REPLY.</summary>
    public static byte[] ComputeEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey)
        => HMACSHA256.HashData(handshakeKey, publicKey);

    /// <summary>Constant-time verify of a peer's <c>ecdhSignature</c> over its public key.</summary>
    public static bool VerifyEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signature)
    {
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(handshakeKey, publicKey, expected);
        return CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    /// <summary>
    /// Generate an ephemeral ECDH keypair on the given curve, returning the handle and its uncompressed public
    /// key (65 bytes for P-256, 133 for P-521).
    /// </summary>
    public static (ECDiffieHellman KeyPair, byte[] PublicKey) GenerateKeyPair(HalyardStreamCurve curve = HalyardStreamCurve.NistP256)
    {
        int cs = CoordinateSize(curve);
        var ecdh = ECDiffieHellman.Create(ToEcCurve(curve));
        var p = ecdh.PublicKey.ExportParameters();
        var pub = new byte[1 + 2 * cs];
        pub[0] = 0x04;
        CopyFieldElement(p.Q.X!, pub.AsSpan(1, cs));
        CopyFieldElement(p.Q.Y!, pub.AsSpan(1 + cs, cs));
        return (ecdh, pub);
    }

    /// <summary>
    /// Compute the raw ECDH shared secret (the X coordinate) from our private key and the peer's uncompressed
    /// public key. The curve is auto-detected from the key length (65 → P-256, 133 → P-521); the result is 32
    /// or 66 bytes respectively.
    /// </summary>
    public static byte[] DeriveSharedSecret(ECDiffieHellman localKeyPair, ReadOnlySpan<byte> peerPublicKey)
    {
        HalyardStreamCurve curve = peerPublicKey.Length switch
        {
            PublicKeyLength => HalyardStreamCurve.NistP256,
            PublicKeyLengthP521 => HalyardStreamCurve.NistP521,
            _ => throw new ArgumentException("Peer public key must be a 65-byte P-256 or 133-byte P-521 uncompressed point.", nameof(peerPublicKey)),
        };
        if (peerPublicKey[0] != 0x04)
            throw new ArgumentException("Peer public key must be an uncompressed point (0x04 prefix).", nameof(peerPublicKey));

        int cs = CoordinateSize(curve);
        var parameters = new ECParameters
        {
            Curve = ToEcCurve(curve),
            Q = new ECPoint
            {
                X = peerPublicKey.Slice(1, cs).ToArray(),
                Y = peerPublicKey.Slice(1 + cs, cs).ToArray(),
            },
        };
        using var peer = ECDiffieHellman.Create(parameters);
        return localKeyPair.DeriveRawSecretAgreement(peer.PublicKey);
    }

    /// <summary>Right-align a field element into a fixed-size span (handles a leading-zero-trimmed export).</summary>
    private static void CopyFieldElement(ReadOnlySpan<byte> element, Span<byte> destination)
    {
        destination.Clear();
        element.CopyTo(destination[(destination.Length - element.Length)..]);
    }

    /// <summary>
    /// Derive the AES-128 key and 16-byte base IV for one channel direction (SP 800-108 counter mode, one
    /// block):
    /// <code>
    /// info  = 0x01 || dir || 0x00 || handshakeKey(16) || 0x01 0x00     (21 bytes; 0x0100 = 256 output bits)
    /// block = HMAC-SHA256(sharedSecret, info)      (sharedSecret = the ECDH X, 32 or 66 bytes; HMAC hashes it)
    /// aesKey = block[0:16] ; baseIv = block[16:32]
    /// </code>
    /// Live-validated (2026-07-22): reproduces a real console's per-direction send/recv key+IV byte-for-byte
    /// (P-521 X, dumped handshakeKey). The prior length field <c>0x00 0x01</c> was a reference-guess error.
    /// </summary>
    public static DirectionKeys DeriveDirection(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> handshakeKey, byte direction)
    {
        if (sharedSecret.IsEmpty)
            throw new ArgumentException("Shared secret must be non-empty.", nameof(sharedSecret));
        if (handshakeKey.Length != 16)
            throw new ArgumentException("handshakeKey must be 16 bytes.", nameof(handshakeKey));

        Span<byte> info = stackalloc byte[21];
        info[0] = 0x01;
        info[1] = direction;
        info[2] = 0x00;
        handshakeKey.CopyTo(info[3..]);
        info[19] = 0x01; // big-endian output length in bits: 0x0100 = 256
        info[20] = 0x00;

        Span<byte> block = stackalloc byte[32];
        HMACSHA256.HashData(sharedSecret, info, block);

        return new DirectionKeys(block[..AesKeyLength].ToArray(), block[AesKeyLength..].ToArray());
    }
}
