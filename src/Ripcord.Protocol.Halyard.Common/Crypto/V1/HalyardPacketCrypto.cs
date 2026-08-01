using System.Numerics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Ripcord.Core.Net.Crypto;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V1;

/// <summary>
/// The v1 per-packet stream crypto for one channel direction: AES-128-CTR payload encryption plus a
/// 4-byte GMAC, both keyed off the packet's 64-bit key position. Encrypt-then-MAC — the tag is computed
/// over the (already encrypted) packet with its tag field zeroed, so a receiver verifies the GMAC first,
/// then AES-CTR-decrypts the payload (both operations are symmetric).
///
/// <para>
/// Key material comes from <see cref="HalyardStreamKeySchedule.DeriveDirection"/> (a per-direction
/// <c>aesKey</c> + <c>baseIv</c>). The cipher key is <c>aesKey</c> used directly (no rotation); the GMAC
/// key rotates every <see cref="RotationWindow"/> key-position units.
/// </para>
///
/// Nonces (all 16 bytes, little-endian addition into the base IV):
/// <list type="bullet">
///   <item><description>GMAC nonce = baseIv + keyPos/16</description></item>
///   <item><description>CTR nonce  = baseIv + keyPos/16 + 1 (one block above the GMAC's; the GMAC
///   conceptually consumes keystream block 0, GCM-style)</description></item>
/// </list>
/// </summary>
public sealed class HalyardPacketCrypto : IDisposable
{
    public const int TagLength = 4;
    public const int BlockSize = 16;
    public const long RotationWindow = 45000;
    public const long RotationFactor = 0xAF6E;

    /// <summary>A/V (video/audio) packets: 4-byte tag at offset 10.</summary>
    public const int AvTagOffset = 10;

    /// <summary>Input/feedback packets: 4-byte tag at offset 8.</summary>
    public const int FeedbackTagOffset = 8;

    private readonly byte[] _aesKey;
    private readonly byte[] _baseIv;
    private readonly byte[] _gmacBaseKey; // key0 = sha256_fold(aesKey || baseIv); the window-0 GMAC key

    /// <summary>
    /// The payload block cipher, created once. The payload key does NOT rotate — <see cref="_aesKey"/> is fixed
    /// for the direction and only the CTR counter varies per packet — so one key expansion serves the whole
    /// session. (The GMAC key does rotate; see <see cref="GmacKey"/>, which is deliberately left uncached.)
    /// </summary>
    private readonly Aes _payloadAes;

    public HalyardPacketCrypto(ReadOnlySpan<byte> aesKey, ReadOnlySpan<byte> baseIv)
    {
        if (aesKey.Length != BlockSize) throw new ArgumentException("AES key must be 16 bytes.", nameof(aesKey));
        if (baseIv.Length != BlockSize) throw new ArgumentException("Base IV must be 16 bytes.", nameof(baseIv));
        _aesKey = aesKey.ToArray();
        _baseIv = baseIv.ToArray();
        _gmacBaseKey = new byte[BlockSize];
        Fold(_aesKey, _baseIv, _gmacBaseKey);
        _payloadAes = AesKeystreamModes.CreateReusableEcb(_aesKey);
    }

    public HalyardPacketCrypto(in HalyardStreamKeySchedule.DirectionKeys keys)
        : this(keys.AesKey, keys.BaseIv) { }

    // ---- payload cipher (§5.5a) ----------------------------------------------------------------

    /// <summary>AES-128-CTR encrypt/decrypt a payload (symmetric) at the given key position.</summary>
    public byte[] CryptPayload(ulong keyPos, ReadOnlySpan<byte> payload)
    {
        var output = payload.ToArray();
        CryptPayloadInPlace(keyPos, output);
        return output;
    }

    /// <summary>
    /// AES-128-CTR encrypt/decrypt a payload in place (symmetric), reusing the cached block cipher.
    ///
    /// <para>
    /// This is the A/V hot path: preferred over <see cref="CryptPayload"/> because it neither allocates a result
    /// buffer nor re-expands the AES key per packet.
    /// </para>
    /// </summary>
    public void CryptPayloadInPlace(ulong keyPos, Span<byte> payload)
    {
        Span<byte> nonce = stackalloc byte[BlockSize];
        CtrNonce(keyPos, nonce);
        // The PS5 packet cipher increments the CTR counter little-endian (matches its GMAC nonce derivation).
        AesKeystreamModes.CtrInPlace(_payloadAes, nonce, payload, littleEndianCounter: true);
    }

    // ---- authentication (§5.5b) ----------------------------------------------------------------

    /// <summary>
    /// Compute the 4-byte GMAC tag over an entire packet whose tag field (at <paramref name="tagOffset"/>)
    /// is treated as zero. The packet is the on-the-wire (encrypted-payload) form.
    /// </summary>
    public byte[] ComputeTag(ulong keyPos, ReadOnlySpan<byte> packet, int tagOffset = AvTagOffset, bool zeroKeyPos = false)
    {
        // AAD = whole packet with the 4-byte GMAC tag zeroed. For CONTROL packets the 4-byte key position
        // that immediately follows the tag is ALSO zeroed; for A/V it is NOT (validated byte-for-byte against
        // the live console: video/audio tag-only, control tag+key_pos). Both directions, all key positions.
        var aad = packet.ToArray();
        int clear = Math.Min(TagLength + (zeroKeyPos ? 4 : 0), aad.Length - tagOffset);
        aad.AsSpan(tagOffset, clear).Clear();

        Span<byte> gmacKey = stackalloc byte[BlockSize];
        GmacKey(keyPos, gmacKey);
        Span<byte> nonce = stackalloc byte[BlockSize];
        GmacNonce(keyPos, nonce);

        var fullTag = AesGcmCore.Mac(gmacKey, nonce, aad);
        return fullTag[..TagLength];
    }

    /// <summary>Write the computed tag into a packet's tag field (sender side); returns the sealed packet.</summary>
    public byte[] SealPacket(ulong keyPos, ReadOnlySpan<byte> packet, int tagOffset = AvTagOffset)
    {
        var sealed_ = packet.ToArray();
        var tag = ComputeTag(keyPos, sealed_, tagOffset);
        tag.CopyTo(sealed_.AsSpan(tagOffset, TagLength));
        return sealed_;
    }

    /// <summary>Verify a packet's 4-byte tag (receiver side), constant-time.</summary>
    public bool VerifyPacket(ulong keyPos, ReadOnlySpan<byte> packet, int tagOffset = AvTagOffset)
    {
        ReadOnlySpan<byte> onWire = packet.Slice(tagOffset, TagLength);
        var expected = ComputeTag(keyPos, packet, tagOffset);
        return CryptographicOperations.FixedTimeEquals(expected, onWire);
    }

    // ---- key/nonce derivation ------------------------------------------------------------------

    /// <summary>
    /// The rotating GMAC key. A base key is folded once from the AES key and base IV
    /// (<c>key0 = sha256_fold(aesKey || baseIv)</c>); window 0 uses it directly, and each later window
    /// re-folds <em>key0</em> (not the AES key) with an incrementing IV:
    /// <code>key[n] = sha256_fold(key0 || iv_add(baseIv, n * 0xAF6E))   (n >= 1)</code>
    /// Recovered and verified byte-for-byte against a live console capture (13670/13672 A/V packets over 223
    /// rotation windows) - the earlier "sha256_fold(aesKey || ...)" form was correct only for window 0.
    /// </summary>
    public void GmacKey(ulong keyPos, Span<byte> destination)
    {
        long window = (long)(keyPos / (ulong)RotationWindow);
        if (window == 0)
        {
            _gmacBaseKey.CopyTo(destination);
            return;
        }

        Span<byte> rotatedIv = stackalloc byte[BlockSize];
        IvAdd(_baseIv, (BigInteger)window * RotationFactor, rotatedIv);
        Fold(_gmacBaseKey, rotatedIv, destination);
    }

    /// <summary>sha256_fold(a || b) = SHA-256(a‖b)[0:16] XOR SHA-256(a‖b)[16:32], for two 16-byte inputs.</summary>
    private static void Fold(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> destination)
    {
        Span<byte> input = stackalloc byte[BlockSize * 2];
        a.CopyTo(input);
        b.CopyTo(input[BlockSize..]);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        for (int i = 0; i < BlockSize; i++)
            destination[i] = (byte)(digest[i] ^ digest[i + BlockSize]);
    }

    /// <summary>GMAC nonce = baseIv + keyPos/16.</summary>
    public void GmacNonce(ulong keyPos, Span<byte> destination)
        => IvAddFast(_baseIv, keyPos >> 4, destination);

    /// <summary>CTR nonce = baseIv + (keyPos + 16)/16 = GMAC nonce + 1.</summary>
    public void CtrNonce(ulong keyPos, Span<byte> destination)
        => IvAddFast(_baseIv, (keyPos + BlockSize) >> 4, destination);

    /// <summary>
    /// Add a 64-bit value to a 16-byte little-endian IV, modulo 2^128 — as two <see cref="ulong"/> halves with
    /// a carry, so it allocates nothing.
    ///
    /// <para>
    /// The <see cref="IvAdd"/> BigInteger form below is correct but allocates, and the two nonce derivations
    /// above run on every single packet (~3600 calls/second at 20 Mbps between them). Both callers pass an
    /// addend derived from a <see cref="ulong"/> key position, so 64 bits is always sufficient; the wider
    /// BigInteger path is kept for the GMAC key rotation, which happens once per 45000 key positions and can
    /// exceed 64 bits when multiplied by the rotation factor.
    /// </para>
    /// </summary>
    internal static void IvAddFast(ReadOnlySpan<byte> iv, ulong addend, Span<byte> destination)
    {
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(iv);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(iv[8..]);

        ulong sum = low + addend;
        if (sum < low)
        {
            high++; // carry into the high half; high wrapping is the mod-2^128 behaviour we want
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, sum);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], high);
    }

    /// <summary>Add a non-negative integer to a 16-byte little-endian IV, modulo 2^128.</summary>
    internal static void IvAdd(ReadOnlySpan<byte> iv, BigInteger addend, Span<byte> destination)
    {
        // Interpret the IV as a little-endian unsigned integer, add, and write back little-endian.
        Span<byte> le = stackalloc byte[BlockSize + 1]; // +1 to force non-negative BigInteger
        iv.CopyTo(le);
        var value = new BigInteger(le, isUnsigned: true, isBigEndian: false);
        value = (value + addend) & Mask128;

        destination.Clear();
        Span<byte> outBytes = stackalloc byte[BlockSize + 1];
        if (!value.TryWriteBytes(outBytes, out int written, isUnsigned: true, isBigEndian: false))
            throw new InvalidOperationException("IV add overflow.");
        outBytes[..Math.Min(written, BlockSize)].CopyTo(destination);
    }

    private static readonly BigInteger Mask128 = (BigInteger.One << 128) - 1;

    /// <summary>Release the cached payload block cipher.</summary>
    public void Dispose() => _payloadAes.Dispose();
}
