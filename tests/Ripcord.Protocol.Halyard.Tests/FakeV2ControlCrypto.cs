using System.Buffers.Binary;
using System.Security.Cryptography;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// A deterministic AEAD double matching <see cref="IHalyardV2ControlCrypto"/>'s shape: ciphertext equals the
/// plaintext (same length), the 16-byte "tag" binds the AAD + counter, and open reverses seal. It does not
/// distinguish the two directions — it stands in for the GCM envelope so the RPCS framing/message tests exercise
/// those layers rather than AES-GCM (whose bytes are KAT'd in the V2 control + stream tests).
/// </summary>
internal sealed class FakeV2ControlCrypto : IHalyardV2ControlCrypto
{
    public bool IsControlEstablished => true;
    public byte[] GenerateControlPublicKey() => throw new NotSupportedException();
    public byte[] ComputeRpHmac(ReadOnlySpan<byte> k16, ReadOnlySpan<byte> publicKey) => throw new NotSupportedException();
    public bool EstablishControl(ReadOnlySpan<byte> consolePublicKey, ReadOnlySpan<byte> k16) => true;

    public byte[] SealClientToConsole(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, ulong counter)
        => [.. plaintext, .. Tag(aad, plaintext, counter)];

    public byte[]? OpenConsoleToClient(ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad, ulong counter)
    {
        if (ciphertextAndTag.Length < 16)
            return null;
        ReadOnlySpan<byte> ct = ciphertextAndTag[..^16];
        ReadOnlySpan<byte> tag = ciphertextAndTag[^16..];
        if (!CryptographicOperations.FixedTimeEquals(tag, Tag(aad, ct, counter)))
            return null;
        return ct.ToArray();
    }

    private static byte[] Tag(ReadOnlySpan<byte> aad, ReadOnlySpan<byte> data, ulong counter)
    {
        Span<byte> ctr = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(ctr, counter);
        byte[] input = [.. aad, .. data, .. ctr];
        return SHA256.HashData(input)[..16];
    }
}
