using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V1;

/// <summary>
/// Per-field IV derivation for the v1 control-plane field cipher and the streaminfo cipher:
/// <code>IV = HMAC-SHA256(contextKey, material || big-endian-uint64(counter))[0:16]</code>
/// where <c>material</c> is the KDF's <c>out2</c>, <c>counter</c> is the field's position in the
/// per-connection sequence (a single counter that increments once per field-encrypt call), and
/// <c>contextKey</c> is chosen by <see cref="HalyardFieldContextKeys.Select"/>.
/// </summary>
public static class HalyardFieldIv
{
    public const int IvLength = 16;
    public const int MaterialLength = 16;

    /// <summary>Derive the 16-byte field IV for the given context key, material, and counter.</summary>
    public static byte[] Derive(ReadOnlySpan<byte> contextKey, ReadOnlySpan<byte> material, ulong counter)
    {
        if (material.Length != MaterialLength)
            throw new ArgumentException("Material must be 16 bytes.", nameof(material));

        Span<byte> message = stackalloc byte[MaterialLength + sizeof(ulong)];
        material.CopyTo(message);
        BinaryPrimitives.WriteUInt64BigEndian(message[MaterialLength..], counter);

        Span<byte> full = stackalloc byte[32];
        HMACSHA256.HashData(contextKey, message, full);

        return full[..IvLength].ToArray();
    }
}
