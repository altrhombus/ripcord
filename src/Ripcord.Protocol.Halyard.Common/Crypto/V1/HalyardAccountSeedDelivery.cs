using System.Security.Cryptography;
using System.Text;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V1;

/// <summary>
/// The account ("web"/no-PIN) registration seed delivery.
///
/// <para>
/// Unlike the PIN route — where the transport key folds an on-console passcode into
/// <c>registrationTable[selector]</c> — the account route obtains its 16-byte registration <b>seed</b> from
/// the console itself, delivered <b>encrypted over the PSN cloud</b>. The client generates two ephemeral
/// 16-byte values and sends them in the cloudAssistedNavigation <c>commands</c> call as <c>data1</c> (the
/// field-cipher <b>key</b>) and <c>data2</c> (the field-cipher <b>material</b>); the console generates the
/// seed, field-encrypts it with them, and returns the ciphertext as <c>customData1</c> (a <b>double-base64</b>
/// value) on the <c>rps:customData1:updated</c> push channel. The client decrypts it here.
/// </para>
///
/// <para>
/// The cipher is the ordinary §2.1 field cipher (<see cref="HalyardControlFieldCrypto"/>: AES-128-CFB,
/// <c>IV = HMAC-SHA256(contextKey, data2‖be64(0))[:16]</c>, <c>contextKey</c> the bundled PS5 HMAC key,
/// counter 0) — no new primitive. The recovered seed then drives the transport key
/// <c>key' = seed XOR registrationTable[ context[selectorOffset] &amp; 0x1f ]</c> (see
/// <see cref="HalyardRegistrationCipher.BuildAccountRequest"/>).
/// </para>
///
/// <para>
/// <b>Provenance.</b> Recovered from this project's own captures and binary analysis and verified byte-exact
/// against the vendor client's own registration seed on multiple fresh account pairings (2026-08-31); the
/// derivation and the wire mapping are recorded in <c>docs/protocol/ps5-cloud-session-api.md</c> and
/// <c>ps5-session-establishment.md</c>. No third-party implementation was consulted.
/// </para>
/// </summary>
public static class HalyardAccountSeedDelivery
{
    /// <summary>The seed, key, and material are each 16 bytes.</summary>
    public const int KeyLength = 16;

    /// <summary>
    /// Generate the ephemeral per-connect <c>data1</c> (field-cipher key) and <c>data2</c> (field-cipher
    /// material) the client sends in the cloud <c>commands</c> call. Both are fresh random 16-byte values.
    /// </summary>
    public static (byte[] Data1, byte[] Data2) GenerateEphemeralKeyMaterial()
        => (RandomNumberGenerator.GetBytes(KeyLength), RandomNumberGenerator.GetBytes(KeyLength));

    /// <summary>
    /// Decode the <c>customData1</c> push value to the seed ciphertext. It is transmitted <b>double-base64</b>:
    /// the outer base64 wraps an inner base64 string, which decodes to the raw ciphertext.
    /// </summary>
    public static byte[] DecodeCustomData1(string customData1)
    {
        ArgumentException.ThrowIfNullOrEmpty(customData1);
        string inner = Encoding.ASCII.GetString(Convert.FromBase64String(customData1));
        return Convert.FromBase64String(inner);
    }

    /// <summary>
    /// Recover the 16-byte registration seed from the delivered <paramref name="ciphertext"/> (the decoded
    /// <c>customData1</c>) using the client's <paramref name="data1"/>/<paramref name="data2"/> and the
    /// bundled <paramref name="contextKey"/>. The console field-encrypts a payload whose first 16 bytes are
    /// the seed; the remainder (if any) is ignored.
    /// </summary>
    public static byte[] RecoverSeed(
        ReadOnlySpan<byte> data1, ReadOnlySpan<byte> data2, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> contextKey)
    {
        if (data1.Length != KeyLength) throw new ArgumentException("data1 (key) must be 16 bytes.", nameof(data1));
        if (data2.Length != KeyLength) throw new ArgumentException("data2 (material) must be 16 bytes.", nameof(data2));
        if (ciphertext.Length < KeyLength) throw new ArgumentException("Ciphertext is shorter than the seed.", nameof(ciphertext));

        byte[] plaintext = new HalyardControlFieldCrypto(data1, data2, contextKey)
            .DecryptField(HalyardRegistrationCipher.FieldCounter, ciphertext);
        return plaintext.AsSpan(0, KeyLength).ToArray();
    }

    /// <summary>
    /// Convenience overload taking the raw <c>customData1</c> push string (decodes then decrypts).
    /// </summary>
    public static byte[] RecoverSeed(
        ReadOnlySpan<byte> data1, ReadOnlySpan<byte> data2, string customData1, ReadOnlySpan<byte> contextKey)
        => RecoverSeed(data1, data2, DecodeCustomData1(customData1), contextKey);

    /// <summary>
    /// The console-side inverse (used by tests and any console emulation): field-<b>encrypt</b> a
    /// <paramref name="seed"/> with the client-supplied <paramref name="data1"/>/<paramref name="data2"/>,
    /// producing the ciphertext that ships as <c>customData1</c>.
    /// </summary>
    public static byte[] SealSeed(
        ReadOnlySpan<byte> data1, ReadOnlySpan<byte> data2, ReadOnlySpan<byte> seed, ReadOnlySpan<byte> contextKey)
    {
        if (seed.Length != KeyLength) throw new ArgumentException("Seed must be 16 bytes.", nameof(seed));
        return new HalyardControlFieldCrypto(data1, data2, contextKey)
            .EncryptField(HalyardRegistrationCipher.FieldCounter, seed);
    }

    /// <summary>Encode a seed ciphertext as the double-base64 <c>customData1</c> wire value.</summary>
    public static string EncodeCustomData1(ReadOnlySpan<byte> ciphertext)
        => Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToBase64String(ciphertext)));
}
