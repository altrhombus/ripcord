using System.Buffers.Binary;
using System.Runtime.Intrinsics;
// Aliased, not imported: System.Runtime.Intrinsics.X86 also declares an `Aes` class, which would
// collide with System.Security.Cryptography.Aes used throughout this file.
using X86 = System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;

namespace Ripcord.Core.Net.Crypto;

/// <summary>
/// AES-128 in Galois/Counter Mode (NIST SP 800-38D), implemented from the specification because the
/// platform <see cref="AesGcm"/> restricts the nonce to 12 bytes, while the Halyard stream MAC uses a
/// full 16-byte GCM IV (which requires the GHASH-based J0 derivation for non-96-bit IVs). This is a
/// generic primitive: full GCM encrypt/decrypt plus a GMAC (authentication-only) path. Correctness is
/// anchored by <c>AesGcmCoreTests</c> against the published SP 800-38D / GCM test vectors, including a
/// non-96-bit-IV case.
/// </summary>
public static class AesGcmCore
{
    private const int BlockSize = 16;

    /// <summary>
    /// GCM encrypt. Returns ciphertext (same length as plaintext) and a full 16-byte authentication tag.
    /// Callers may truncate the tag (the Halyard packet MAC keeps the first 4 bytes).
    /// </summary>
    public static (byte[] Ciphertext, byte[] Tag) Encrypt(
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plaintext)
    {
        using var aes = CreateEcb(key);

        Span<byte> h = stackalloc byte[BlockSize];
        aes.EncryptEcb(stackalloc byte[BlockSize], h, PaddingMode.None); // H = E_K(0^128)

        Span<byte> j0 = stackalloc byte[BlockSize];
        ComputeJ0(aes, h, iv, j0);

        // ciphertext = GCTR(inc32(J0), plaintext)
        Span<byte> icb = stackalloc byte[BlockSize];
        j0.CopyTo(icb);
        Inc32(icb);
        var ciphertext = GCtr(aes, icb, plaintext);

        // S = GHASH(AAD || 0-pad || C || 0-pad || [len(AAD)]_64 || [len(C)]_64)
        Span<byte> s = stackalloc byte[BlockSize];
        GHashAadAndCipher(h, aad, ciphertext, s);

        // tag = GCTR(J0, S) = E_K(J0) XOR S  (single block)
        Span<byte> tag = stackalloc byte[BlockSize];
        aes.EncryptEcb(j0, tag, PaddingMode.None);
        for (int i = 0; i < BlockSize; i++)
            tag[i] ^= s[i];

        return (ciphertext, tag.ToArray());
    }

    /// <summary>
    /// GMAC: the GCM authentication tag over <paramref name="aad"/> with an empty plaintext. Equivalent to
    /// <see cref="Encrypt"/> with no plaintext but avoids the (unused) ciphertext allocation.
    /// </summary>
    public static byte[] Mac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> aad)
    {
        using var aes = CreateEcb(key);

        Span<byte> h = stackalloc byte[BlockSize];
        aes.EncryptEcb(stackalloc byte[BlockSize], h, PaddingMode.None);

        Span<byte> j0 = stackalloc byte[BlockSize];
        ComputeJ0(aes, h, iv, j0);

        Span<byte> s = stackalloc byte[BlockSize];
        GHashAadAndCipher(h, aad, ReadOnlySpan<byte>.Empty, s);

        Span<byte> tag = stackalloc byte[BlockSize];
        aes.EncryptEcb(j0, tag, PaddingMode.None);
        for (int i = 0; i < BlockSize; i++)
            tag[i] ^= s[i];

        return tag.ToArray();
    }

    private static Aes CreateEcb(ReadOnlySpan<byte> key)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        return aes;
    }

    private static void ComputeJ0(Aes aes, ReadOnlySpan<byte> h, ReadOnlySpan<byte> iv, Span<byte> j0)
    {
        if (iv.Length == 12)
        {
            // 96-bit IV: J0 = IV || 0^31 || 1
            iv.CopyTo(j0);
            j0[12] = 0;
            j0[13] = 0;
            j0[14] = 0;
            j0[15] = 1;
            return;
        }

        // Non-96-bit IV: J0 = GHASH_H( IV || 0^s || [len(IV)]_128 ), where the final block is
        // 64 zero bits followed by the 64-bit bit-length of the IV.
        Span<byte> y = stackalloc byte[BlockSize];
        y.Clear();

        int full = iv.Length / BlockSize;
        for (int i = 0; i < full; i++)
        {
            XorInto(y, iv.Slice(i * BlockSize, BlockSize));
            GfMul(y, h);
        }
        int rem = iv.Length - full * BlockSize;
        if (rem > 0)
        {
            Span<byte> block = stackalloc byte[BlockSize];
            block.Clear();
            iv.Slice(full * BlockSize, rem).CopyTo(block);
            XorInto(y, block);
            GfMul(y, h);
        }

        Span<byte> lenBlock = stackalloc byte[BlockSize];
        lenBlock.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(lenBlock[8..], (ulong)iv.Length * 8);
        XorInto(y, lenBlock);
        GfMul(y, h);

        y.CopyTo(j0);
    }

    private static void GHashAadAndCipher(ReadOnlySpan<byte> h, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> cipher, Span<byte> result)
    {
        Span<byte> y = stackalloc byte[BlockSize];
        y.Clear();
        GHashData(y, h, aad);
        GHashData(y, h, cipher);

        Span<byte> lenBlock = stackalloc byte[BlockSize];
        BinaryPrimitives.WriteUInt64BigEndian(lenBlock[..8], (ulong)aad.Length * 8);
        BinaryPrimitives.WriteUInt64BigEndian(lenBlock[8..], (ulong)cipher.Length * 8);
        XorInto(y, lenBlock);
        GfMul(y, h);

        y.CopyTo(result);
    }

    private static void GHashData(Span<byte> y, ReadOnlySpan<byte> h, ReadOnlySpan<byte> data)
    {
        int full = data.Length / BlockSize;
        for (int i = 0; i < full; i++)
        {
            XorInto(y, data.Slice(i * BlockSize, BlockSize));
            GfMul(y, h);
        }
        int rem = data.Length - full * BlockSize;
        if (rem > 0)
        {
            Span<byte> block = stackalloc byte[BlockSize];
            block.Clear();
            data.Slice(full * BlockSize, rem).CopyTo(block);
            XorInto(y, block);
            GfMul(y, h);
        }
    }

    /// <summary>GCTR: XOR the data with the keystream produced from a starting counter block (32-bit inc).</summary>
    private static byte[] GCtr(Aes aes, Span<byte> counter, ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        Span<byte> keystream = stackalloc byte[BlockSize];
        for (int offset = 0; offset < data.Length; offset += BlockSize)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
            int n = Math.Min(BlockSize, data.Length - offset);
            for (int j = 0; j < n; j++)
                output[offset + j] = (byte)(data[offset + j] ^ keystream[j]);
            Inc32(counter);
        }
        return output;
    }

    /// <summary>Increment only the rightmost 32 bits of the counter block (GCM inc32).</summary>
    private static void Inc32(Span<byte> counter)
    {
        for (int i = 15; i >= 12; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }

    private static void XorInto(Span<byte> target, ReadOnlySpan<byte> value)
    {
        for (int i = 0; i < BlockSize; i++)
            target[i] ^= value[i];
    }

    /// <summary>
    /// Multiply two 128-bit blocks in GF(2^128) using the GCM reduction polynomial (bit-reflected,
    /// R = 0xE1 || 0^120). Operates in place: <paramref name="x"/> := x · y.
    ///
    /// <para>
    /// Dispatches to a carry-less-multiply implementation where the hardware has it. This is the single
    /// dominant cost in the A/V receive path: GHASH calls this once per 16-byte block (~90 times for a full
    /// packet), and the portable bit-serial form below runs 128 iterations of byte-wise XOR and shift per call
    /// — measured at ~1.7 us each, i.e. ~150 us per packet, which alone capped the receive path near 60 Mbps.
    /// The PCLMULQDQ path is ~60x faster.
    /// </para>
    /// </summary>
    internal static void GfMul(Span<byte> x, ReadOnlySpan<byte> y)
    {
        // No ARM64 path, deliberately. The equivalent primitive exists
        // (System.Runtime.Intrinsics.Arm.Aes.PolynomialMultiplyWideningLower/Upper), and Windows-on-ARM or an ARM
        // handheld would otherwise take the bit-serial fallback that WAS the 1080p60 bottleneck (~63x slower).
        // It is still not written, because the x86 version here was wrong for essentially every input on the first
        // attempt — a missing 256-bit shift before reduction, 0 of 3000 vectors passing — and that was caught only
        // by executing it against the bit-serial oracle. Nothing in this environment can execute ARM64, so an ARM
        // implementation would ship unverified. Slow-and-correct beats fast-and-unverified for stream crypto.
        // GfMulHardwareTests would validate it the moment anyone runs the suite on ARM64; write it there.
        if (X86.Pclmulqdq.IsSupported && X86.Ssse3.IsSupported)
        {
            GfMulCarryless(x, y);
            return;
        }

        GfMulBitSerial(x, y);
    }

    /// <summary>
    /// Hardware GF(2^128) multiply. GCM's field is bit-reflected relative to the natural little-endian
    /// interpretation, so operands are byte-reversed into the reflected domain, multiplied carry-lessly, reduced
    /// modulo the GCM polynomial, and reversed back.
    /// </summary>
    internal static void GfMulCarryless(Span<byte> x, ReadOnlySpan<byte> y)
    {
        Vector128<ulong> a = Reflect(Vector128.Create(x)).AsUInt64();
        Vector128<ulong> b = Reflect(Vector128.Create(y)).AsUInt64();

        // 128x128 -> 256 carry-less product, schoolbook with the cross terms folded.
        Vector128<ulong> low = X86.Pclmulqdq.CarrylessMultiply(a, b, 0x00);
        Vector128<ulong> high = X86.Pclmulqdq.CarrylessMultiply(a, b, 0x11);
        Vector128<ulong> mid = X86.Sse2.Xor(
            X86.Pclmulqdq.CarrylessMultiply(a, b, 0x10),
            X86.Pclmulqdq.CarrylessMultiply(a, b, 0x01));

        low = X86.Sse2.Xor(low, X86.Sse2.ShiftLeftLogical128BitLane(mid.AsByte(), 8).AsUInt64());
        high = X86.Sse2.Xor(high, X86.Sse2.ShiftRightLogical128BitLane(mid.AsByte(), 8).AsUInt64());

        // Shift the 256-bit product left by one bit before reducing.
        //
        // This step is not optional and is easy to omit: GCM's field is bit-reflected, and reflecting both
        // operands shifts the product by one power of x relative to the plain carry-less result. Without it the
        // multiply is wrong for essentially every input — verified against the bit-serial reference, which
        // matched 0/3000 random cases before this and 3000/3000 after.
        (high, low) = ShiftLeft256By1(high, low);

        // Two-step reduction by the reflected GCM polynomial (0xC200000000000000), each fold folding the low
        // half upward. SwapHalves is the qword exchange used between folds.
        Vector128<ulong> poly = Vector128.Create(0xC200000000000000UL, 0UL);
        Vector128<ulong> fold = X86.Pclmulqdq.CarrylessMultiply(low, poly, 0x00);
        low = X86.Sse2.Xor(SwapHalves(low), fold);
        fold = X86.Pclmulqdq.CarrylessMultiply(low, poly, 0x00);
        low = X86.Sse2.Xor(SwapHalves(low), fold);

        Reflect(X86.Sse2.Xor(high, low).AsByte()).CopyTo(x);
    }

    /// <summary>Byte-reverse a 128-bit block, converting between GCM's byte order and the reflected domain.</summary>
    private static Vector128<byte> Reflect(Vector128<byte> value) => X86.Ssse3.Shuffle(
        value,
        Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0));

    /// <summary>Exchange the two 64-bit halves of a 128-bit vector.</summary>
    private static Vector128<ulong> SwapHalves(Vector128<ulong> value) =>
        X86.Sse2.Shuffle(value.AsUInt32(), 0x4E).AsUInt64();

    /// <summary>
    /// Shift a 256-bit value, held as (<paramref name="high"/>, <paramref name="low"/>) with lane 0 least
    /// significant, left by one bit — carrying between lanes and from the low half into the high half.
    /// </summary>
    private static (Vector128<ulong> High, Vector128<ulong> Low) ShiftLeft256By1(
        Vector128<ulong> high, Vector128<ulong> low)
    {
        Vector128<ulong> lowCarry = X86.Sse2.ShiftRightLogical(low, 63);
        Vector128<ulong> highCarry = X86.Sse2.ShiftRightLogical(high, 63);

        // Within each half, lane 0's carry moves up into lane 1.
        Vector128<ulong> shiftedLow = X86.Sse2.Or(
            X86.Sse2.ShiftLeftLogical(low, 1),
            X86.Sse2.ShiftLeftLogical128BitLane(lowCarry.AsByte(), 8).AsUInt64());

        Vector128<ulong> shiftedHigh = X86.Sse2.Or(
            X86.Sse2.ShiftLeftLogical(high, 1),
            X86.Sse2.ShiftLeftLogical128BitLane(highCarry.AsByte(), 8).AsUInt64());

        // And the low half's top bit carries across into the high half's lane 0.
        shiftedHigh = X86.Sse2.Or(
            shiftedHigh,
            X86.Sse2.ShiftRightLogical128BitLane(lowCarry.AsByte(), 8).AsUInt64());

        return (shiftedHigh, shiftedLow);
    }

    /// <summary>
    /// Portable bit-serial GF(2^128) multiply. Retained as the fallback for hardware without carry-less
    /// multiply, and as the reference implementation the hardware path is differentially tested against — it is
    /// the form validated directly by the SP 800-38D vectors.
    /// </summary>
    internal static void GfMulBitSerial(Span<byte> x, ReadOnlySpan<byte> y)
    {
        Span<byte> z = stackalloc byte[BlockSize];
        z.Clear();
        Span<byte> v = stackalloc byte[BlockSize];
        y.CopyTo(v);

        for (int i = 0; i < 128; i++)
        {
            // Bit i of x, taken from the MSB of byte 0 downward.
            int bit = (x[i >> 3] >> (7 - (i & 7))) & 1;
            if (bit == 1)
                for (int k = 0; k < BlockSize; k++)
                    z[k] ^= v[k];

            // v = v >> 1 in GF(2^128); if the bit shifted out (LSB) was 1, reduce with R.
            bool lsb = (v[15] & 1) == 1;
            ShiftRight1(v);
            if (lsb)
                v[0] ^= 0xE1;
        }

        z.CopyTo(x);
    }

    private static void ShiftRight1(Span<byte> v)
    {
        byte carry = 0;
        for (int i = 0; i < BlockSize; i++)
        {
            byte next = (byte)(v[i] << 7);
            v[i] = (byte)((v[i] >> 1) | carry);
            carry = next;
        }
    }
}
