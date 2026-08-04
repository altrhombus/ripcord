using Ripcord.Core.Net.Crypto;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V1;

/// <summary>
/// The v1 control-plane symmetric crypto: the encrypted <c>/sess/ctrl</c> HTTP header fields
/// (AES-128-CFB) and the streaminfo/launchSpec config (AES-128-OFB), both keyed by the control session
/// key <c>out1</c> with per-message IVs derived from <c>out2</c> (see <see cref="HalyardFieldIv"/>).
///
/// <para>
/// Each encrypted field uses a single per-connection counter that fixes the field order (RP-Auth = 0,
/// RP-Did = 1, RP-OSType = 2, RP-StartBitrate = 3, RP-StreamingType = 4). The counter is passed
/// explicitly here so the type stays a pure transform; the message builder owns the ordering.
/// </para>
/// </summary>
public sealed class HalyardControlFieldCrypto
{
    private readonly byte[] _key;       // out1
    private readonly byte[] _material;  // out2
    private readonly byte[] _contextKey;

    public HalyardControlFieldCrypto(ReadOnlySpan<byte> key, ReadOnlySpan<byte> material, ReadOnlySpan<byte> contextKey)
    {
        if (key.Length != 16) throw new ArgumentException("Key must be 16 bytes.", nameof(key));
        if (material.Length != 16) throw new ArgumentException("Material must be 16 bytes.", nameof(material));
        if (contextKey.Length != 16) throw new ArgumentException("Context key must be 16 bytes.", nameof(contextKey));

        _key = key.ToArray();
        _material = material.ToArray();
        _contextKey = contextKey.ToArray();
    }

    /// <summary>
    /// Build the field crypto for a session: run the KDF over <paramref name="nonce"/>‖companion and select
    /// the context key from the negotiated selectors.
    /// </summary>
    public static HalyardControlFieldCrypto FromKdf(
        HalyardControlKdf kdf,
        HalyardFieldContextKeys contextKeys,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> companion,
        int codecSelector,
        int versionSelector)
    {
        var (key, material) = kdf.Derive(nonce, companion, versionSelector);
        var contextKey = contextKeys.Select(codecSelector, versionSelector);
        return new HalyardControlFieldCrypto(key, material, contextKey.Span);
    }

    /// <summary>Encrypt a control field (AES-128-CFB128) and return the raw ciphertext.</summary>
    public byte[] EncryptField(ulong counter, ReadOnlySpan<byte> plaintext)
    {
        var iv = HalyardFieldIv.Derive(_contextKey, _material, counter);
        return AesKeystreamModes.Cfb128(_key, iv, plaintext, encrypt: true);
    }

    /// <summary>Decrypt a control field (AES-128-CFB128) from raw ciphertext.</summary>
    public byte[] DecryptField(ulong counter, ReadOnlySpan<byte> ciphertext)
    {
        var iv = HalyardFieldIv.Derive(_contextKey, _material, counter);
        return AesKeystreamModes.Cfb128(_key, iv, ciphertext, encrypt: false);
    }

    /// <summary>Encrypt a control field and base64-encode it as it appears in the HTTP header.</summary>
    public string EncryptFieldBase64(ulong counter, ReadOnlySpan<byte> plaintext)
        => Convert.ToBase64String(EncryptField(counter, plaintext));

    /// <summary>Decode a base64 header value and decrypt it.</summary>
    public byte[] DecryptFieldBase64(ulong counter, string base64)
        => DecryptField(counter, Convert.FromBase64String(base64));

    /// <summary>
    /// Encrypt or decrypt the streaminfo/launchSpec config (AES-128-OFB, symmetric). The IV uses the same
    /// derivation scheme as the fields, with the supplied <paramref name="counter"/>.
    /// </summary>
    public byte[] StreaminfoCrypt(ulong counter, ReadOnlySpan<byte> data)
    {
        var iv = HalyardFieldIv.Derive(_contextKey, _material, counter);
        return AesKeystreamModes.Ofb(_key, iv, data);
    }

    /// <summary>Encrypt/decrypt streaminfo with an IV supplied directly (e.g. one recovered from a capture).</summary>
    public byte[] StreaminfoCryptWithIv(ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
        => AesKeystreamModes.Ofb(_key, iv, data);
}
