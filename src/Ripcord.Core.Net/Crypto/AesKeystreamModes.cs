using System.Buffers;
using System.Security.Cryptography;

namespace Ripcord.Core.Net.Crypto;

/// <summary>
/// AES-128 keystream cipher modes (CTR, OFB, CFB128) implemented directly on top of the AES block
/// permutation. These are the standard modes from NIST SP 800-38A, implemented here rather than via the
/// platform helpers because the protocol needs stream semantics the one-shot BCL helpers don't offer
/// portably: full-block CFB with a partial trailing block, OFB (no managed one-shot), and a CTR whose
/// initial counter is supplied as a full 16-byte block that then increments across the whole 128-bit
/// width. Correctness is anchored by <c>AesKeystreamModesTests</c> against the SP 800-38A test vectors.
///
/// This is a generic cryptographic primitive with no protocol knowledge; the Halyard key schedules build
/// on it.
/// </summary>
public static class AesKeystreamModes
{
    public const int BlockSize = 16;

    private static Aes CreateEcb(ReadOnlySpan<byte> key)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        return aes;
    }

    /// <summary>
    /// Create a reusable ECB block cipher for <paramref name="key"/>, for callers that process many messages
    /// under one key and should not pay key expansion each time.
    ///
    /// <para>
    /// This exists because the A/V receive path was calling <see cref="Ctr(ReadOnlySpan{byte},
    /// ReadOnlySpan{byte}, ReadOnlySpan{byte}, bool)"/> per datagram — roughly 1800 times a second at 20 Mbps —
    /// and each call did a full <c>Aes.Create()</c> plus key expansion (a CNG key handle on Windows) to produce
    /// a byte-for-byte identical key schedule. The caller owns and disposes the returned instance.
    /// </para>
    /// </summary>
    public static Aes CreateReusableEcb(ReadOnlySpan<byte> key) => CreateEcb(key);

    /// <summary>
    /// AES-128-CTR in place, using a caller-owned block cipher from <see cref="CreateReusableEcb"/>.
    /// <paramref name="data"/> is XORed with the keystream, so this both encrypts and decrypts. Allocation-free.
    /// </summary>
    public static void CtrInPlace(
        Aes aes,
        ReadOnlySpan<byte> initialCounter,
        Span<byte> data,
        bool littleEndianCounter = false)
    {
        ArgumentNullException.ThrowIfNull(aes);
        if (initialCounter.Length != BlockSize)
            throw new ArgumentException("CTR counter must be 16 bytes.", nameof(initialCounter));
        if (data.IsEmpty)
            return;

        // Materialise the whole counter sequence, then generate the entire keystream in ONE EncryptEcb call.
        //
        // The obvious implementation calls EncryptEcb once per 16-byte block, and that is what this used to do.
        // Measured, that costs ~0.64 us per block — around a thousand times the cost of the AES round itself —
        // because the per-call overhead of the one-shot API dominates completely. A 1360-byte packet meant 85
        // such calls (~57 us); batching them into one call is ~40x faster (~1.4 us) and is the difference
        // between the A/V receive path topping out near 50 Mbps and being irrelevant to throughput.
        int blocks = (data.Length + BlockSize - 1) / BlockSize;
        int keystreamBytes = blocks * BlockSize;

        // Pooled rather than stack-allocated: payloads run to ~1.4 KB and the pool costs far less than the
        // per-block call overhead this replaces.
        byte[] scratch = ArrayPool<byte>.Shared.Rent(keystreamBytes * 2);
        try
        {
            Span<byte> counters = scratch.AsSpan(0, keystreamBytes);
            Span<byte> keystream = scratch.AsSpan(keystreamBytes, keystreamBytes);

            // Block i uses initialCounter incremented i times — the same sequence the per-block loop produced.
            initialCounter.CopyTo(counters);
            for (int i = 1; i < blocks; i++)
            {
                Span<byte> current = counters.Slice(i * BlockSize, BlockSize);
                counters.Slice((i - 1) * BlockSize, BlockSize).CopyTo(current);

                // The PS5 stream cipher increments LITTLE-endian; standard CTR (SP 800-38A) big-endian.
                if (littleEndianCounter)
                    IncrementLittleEndian(current);
                else
                    IncrementBigEndian(current);
            }

            aes.EncryptEcb(counters, keystream, PaddingMode.None);

            // XOR only the real data length, so a partial trailing block truncates correctly.
            for (int i = 0; i < data.Length; i++)
                data[i] ^= keystream[i];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// AES-128-CTR (SP 800-38A). <paramref name="initialCounter"/> is the full 16-byte counter block used
    /// for the first keystream block; subsequent blocks increment the entire 128-bit block, big-endian.
    /// Encrypt and decrypt are the same operation.
    /// </summary>
    public static byte[] Ctr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> initialCounter, ReadOnlySpan<byte> data, bool littleEndianCounter = false)
    {
        // One implementation: this overload just owns the buffer and the cipher, then defers to CtrInPlace so
        // both paths share the batched keystream generation (and cannot drift apart).
        //
        // Standard CTR increments big-endian (NIST); the PS5 stream cipher increments LITTLE-ENDIAN (byte 0
        // first, carry up) — the same 128-bit IV addition as its GMAC nonce. Big-endian corrupts every block after the
        // first (NAL header decodes, slice body garbles). Wire-verified against real A/V.
        var output = data.ToArray();
        using var aes = CreateEcb(key);
        CtrInPlace(aes, initialCounter, output, littleEndianCounter);
        return output;
    }

    /// <summary>AES-128-OFB (SP 800-38A). Symmetric; encrypt == decrypt.</summary>
    public static byte[] Ofb(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        if (iv.Length != BlockSize)
            throw new ArgumentException("OFB IV must be 16 bytes.", nameof(iv));

        var output = new byte[data.Length];
        Span<byte> feedback = stackalloc byte[BlockSize];
        iv.CopyTo(feedback);

        using var aes = CreateEcb(key);
        for (int offset = 0; offset < data.Length; offset += BlockSize)
        {
            aes.EncryptEcb(feedback, feedback, PaddingMode.None); // O_{i+1} = E(O_i)
            int n = Math.Min(BlockSize, data.Length - offset);
            for (int j = 0; j < n; j++)
                output[offset + j] = (byte)(data[offset + j] ^ feedback[j]);
        }

        return output;
    }

    /// <summary>
    /// AES-128-CFB with a 128-bit feedback segment (CFB128, SP 800-38A), handling a partial final block.
    /// The feedback register takes the full ciphertext block after each full block.
    /// </summary>
    public static byte[] Cfb128(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data, bool encrypt)
    {
        if (iv.Length != BlockSize)
            throw new ArgumentException("CFB IV must be 16 bytes.", nameof(iv));

        var output = new byte[data.Length];
        Span<byte> feedback = stackalloc byte[BlockSize];
        iv.CopyTo(feedback);
        Span<byte> keystream = stackalloc byte[BlockSize];
        Span<byte> cipherBlock = stackalloc byte[BlockSize];

        using var aes = CreateEcb(key);
        for (int offset = 0; offset < data.Length; offset += BlockSize)
        {
            aes.EncryptEcb(feedback, keystream, PaddingMode.None);
            int n = Math.Min(BlockSize, data.Length - offset);
            for (int j = 0; j < n; j++)
            {
                byte input = data[offset + j];
                byte result = (byte)(input ^ keystream[j]);
                output[offset + j] = result;
                // Feedback always takes the ciphertext: on encrypt that is the output, on decrypt the input.
                cipherBlock[j] = encrypt ? result : input;
            }
            // Only full blocks feed forward; a partial final block ends the stream.
            if (n == BlockSize)
                cipherBlock.CopyTo(feedback);
        }

        return output;
    }

    /// <summary>Increment a 16-byte counter as a 128-bit big-endian integer, wrapping on overflow.</summary>
    public static void IncrementBigEndian(Span<byte> counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }

    /// <summary>Increment a 16-byte counter little-endian (byte 0 first, carry upward) — the console's
    /// addition convention for both the CTR keystream and the GMAC nonce.</summary>
    public static void IncrementLittleEndian(Span<byte> counter)
    {
        for (int i = 0; i < counter.Length; i++)
        {
            if (++counter[i] != 0)
                break;
        }
    }
}
