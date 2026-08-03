using System.Security.Cryptography;
using Ripcord.Protocol.Halyard.Common.Streaming.Fec;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The video FEC math: GF(2^8) field laws and systematic Cauchy Reed-Solomon round-trips.
///
/// <para><b>Scope — these are self-consistency tests, not ground truth.</b> They prove our decoder inverts
/// our encoder. They cannot prove either matches the console, because no capture in the dirty room pairs A/V
/// traffic with usable stream keys (the one session with keys never reached streaming), so there is no
/// console-produced parity unit to compare against. The earlier version of this comment claimed these
/// validated "exactly what the console's encoder produced" — they never did, and that wording is the reason
/// the matrix form went unexamined for so long.</para>
///
/// <para>What backs the console-agreement claim instead is first-party analysis of our own client, on both
/// halves that had to agree: the matrix form is [C]-confirmed by decompilation (see
/// <see cref="CauchyReedSolomon"/>), and as of 2026-08-02 the field it is built over is [V]-confirmed by a
/// runtime dump of the client's inverse table (<see cref="GaloisField256_MatchesConsoleInverseTable"/>).
/// Those were only ever safe as a pair. Round-trip coverage below is still self-consistency, but it now runs
/// over a field and a matrix that are both pinned to the console.</para>
/// </summary>
public class FecTests
{
    [Fact]
    public void GaloisField_InverseAndDivideAreConsistent()
    {
        for (int a = 1; a < 256; a++)
        {
            byte inv = GaloisField256.Inverse((byte)a);
            Assert.Equal(1, GaloisField256.Multiply((byte)a, inv));
            Assert.Equal(inv, GaloisField256.Divide(1, (byte)a));
            // a * b / b == a
            for (int b = 1; b < 256; b += 37)
            {
                byte prod = GaloisField256.Multiply((byte)a, (byte)b);
                Assert.Equal((byte)a, GaloisField256.Divide(prod, (byte)b));
            }
        }
    }

    [Fact]
    public void GaloisField_MultiplyIsCommutativeWithZero()
    {
        Assert.Equal(0, GaloisField256.Multiply(0, 123));
        Assert.Equal(0, GaloisField256.Multiply(123, 0));
        Assert.Equal(123, GaloisField256.Multiply(1, 123));
    }

    /// <summary>
    /// Ground truth: the vendor client's own GF(2^8) division table, dumped from its live field object on
    /// 2026-08-02 (breakpoint at `0x101036db` in the coding-matrix builder `FUN_101035e0`, where the table
    /// base is already in a register). The dumped 0x200 bytes are the first two rows of a 64 KB
    /// `[a &lt;&lt; 8 | b]` divide table; row `a = 1` is `1 / b`, which is the inverse table the matrix
    /// construction indexes as `table[value | 0x100]`. Pinned here as the head and tail of that row — those
    /// bytes are unique to polynomial 0x11d across all 16 primitive degree-8 polynomials, so this fails
    /// loudly if the field is ever changed.
    ///
    /// <para>Index 0 is deliberately excluded: the console stores a 0xff sentinel for the undefined inverse
    /// of zero, where <see cref="GaloisField256.Inverse"/> throws. Nothing indexes it (see
    /// <see cref="CauchyReedSolomon"/>), so the two conventions never diverge in practice.</para>
    /// </summary>
    [Fact]
    public void GaloisField256_MatchesConsoleInverseTable()
    {
        byte[] head = [0x01, 0x8e, 0xf4, 0x47, 0xa7, 0x7a, 0xba, 0xad, 0x9d, 0xdd, 0x98, 0x3d, 0xaa, 0x5d, 0x96];
        for (int a = 1; a <= head.Length; a++)
        {
            Assert.Equal(head[a - 1], GaloisField256.Inverse((byte)a));
        }

        byte[] tail = [0x42, 0xd4, 0xe8, 0x75, 0x7f, 0xff, 0x7e, 0xfd];
        for (int i = 0; i < tail.Length; i++)
        {
            Assert.Equal(tail[i], GaloisField256.Inverse((byte)(248 + i)));
        }

        Assert.Throws<DivideByZeroException>(() => GaloisField256.Inverse(0));
    }

    [Fact]
    public void CauchyMatrix_MatchesReferenceConstruction()
    {
        // matrix[i*k+j] = 1 / (i XOR (m+j)) in GF(2^8) — the standard Cauchy coding-matrix construction.
        const int k = 4, m = 2;
        byte[] matrix = CauchyReedSolomon.BuildCodingMatrix(k, m);
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < k; j++)
            {
                byte expected = GaloisField256.Inverse((byte)(i ^ (m + j)));
                Assert.Equal(expected, matrix[i * k + j]);
            }
        }
    }

    [Theory]
    [InlineData(4, 2, new[] { 0 })]                 // one source unit lost
    [InlineData(4, 2, new[] { 0, 3 })]              // two source units lost (== m)
    [InlineData(6, 3, new[] { 1, 4 })]              // mixed
    [InlineData(6, 3, new[] { 0, 2, 5 })]           // three lost (== m)
    [InlineData(8, 4, new[] { 0, 1, 2, 3 })]        // first four lost
    [InlineData(3, 1, new[] { 2 })]
    public void ReedSolomon_ReconstructsErasedSourceUnits(int k, int m, int[] erasures)
    {
        const int unitSize = 200;
        const int stride = 208; // 16-aligned, > unitSize (mirrors the demuxer's padded stride)
        int total = k + m;

        // Build k random source units + computed parity; keep a pristine copy to compare against.
        var frame = new byte[total * stride];
        for (int u = 0; u < k; u++)
        {
            RandomNumberGenerator.Fill(frame.AsSpan(u * stride, unitSize));
        }
        CauchyReedSolomon.Encode(frame, unitSize, stride, k, m);
        var pristine = (byte[])frame.Clone();

        // Erase the chosen units (zero their slots) and mark them absent.
        var present = new bool[total];
        Array.Fill(present, true);
        foreach (int e in erasures)
        {
            Array.Clear(frame, e * stride, stride);
            present[e] = false;
        }

        Assert.True(CauchyReedSolomon.Decode(frame, unitSize, stride, k, m, present));

        // Every source unit's payload must match the original.
        for (int u = 0; u < k; u++)
        {
            Assert.Equal(
                Convert.ToHexString(pristine.AsSpan(u * stride, unitSize)),
                Convert.ToHexString(frame.AsSpan(u * stride, unitSize)));
        }
    }

    [Fact]
    public void ReedSolomon_LosingAParityUnitStillRecoversSource()
    {
        const int k = 4, m = 2, unitSize = 64, stride = 64, total = k + m;
        var frame = new byte[total * stride];
        for (int u = 0; u < k; u++)
        {
            RandomNumberGenerator.Fill(frame.AsSpan(u * stride, unitSize));
        }
        CauchyReedSolomon.Encode(frame, unitSize, stride, k, m);
        var pristine = (byte[])frame.Clone();

        // Lose one source unit and one parity unit — still k survivors, so recoverable.
        var present = new bool[total];
        Array.Fill(present, true);
        Array.Clear(frame, 1 * stride, stride); present[1] = false;         // source unit 1
        Array.Clear(frame, (k + 0) * stride, stride); present[k + 0] = false; // parity unit 0

        Assert.True(CauchyReedSolomon.Decode(frame, unitSize, stride, k, m, present));
        Assert.Equal(
            Convert.ToHexString(pristine.AsSpan(1 * stride, unitSize)),
            Convert.ToHexString(frame.AsSpan(1 * stride, unitSize)));
    }

    /// <summary>
    /// The slot stride is layout, not protocol: recovery must produce byte-identical results at any stride
    /// that is at least the coded unit length. This is the claim that closed the long-running "is the 0x10
    /// stride a mis-derived wire constant?" question — the console's own stride equals its coded length (a
    /// multiple of 4), and ours is free precisely because coding never touches the tail between the two. If a
    /// future change makes the stride observable in the output, this fails and that reasoning needs revisiting.
    /// </summary>
    [Theory]
    [InlineData(6, 3, 1004)]  // a real cap47 coded length: 4-aligned, not 16-aligned
    [InlineData(6, 3, 1012)]
    [InlineData(4, 2, 999)]   // deliberately not even 4-aligned, to prove the decoder does not care
    public void ReedSolomon_RecoveryIsIndependentOfSlotStride(int k, int m, int unitSize)
    {
        int total = k + m;
        var source = new byte[total][];
        var rng = new Random(unitSize);
        for (int u = 0; u < k; u++)
        {
            source[u] = new byte[unitSize];
            rng.NextBytes(source[u]);
        }

        // Same logical frame, laid out at two different strides.
        byte[] Build(int stride)
        {
            var frame = new byte[total * stride];
            for (int u = 0; u < k; u++)
            {
                source[u].CopyTo(frame.AsSpan(u * stride));
            }
            CauchyReedSolomon.Encode(frame, unitSize, stride, k, m);
            return frame;
        }

        int tight = unitSize;                          // no slack at all
        int aligned16 = (unitSize + 0xf) & ~0xf;       // what the demuxer uses
        byte[] a = Build(tight), b = Build(aligned16);

        var present = new bool[total];
        Array.Fill(present, true);
        foreach (int e in new[] { 0, k - 1 })
        {
            present[e] = false;
        }
        Array.Clear(a, 0, tight);
        Array.Clear(a, (k - 1) * tight, tight);
        Array.Clear(b, 0, aligned16);
        Array.Clear(b, (k - 1) * aligned16, aligned16);

        Assert.True(CauchyReedSolomon.Decode(a, unitSize, tight, k, m, present));
        Assert.True(CauchyReedSolomon.Decode(b, unitSize, aligned16, k, m, present));

        // Both strides must reconstruct the original bytes — and so agree with each other.
        for (int u = 0; u < k; u++)
        {
            Assert.Equal(
                Convert.ToHexString(source[u]),
                Convert.ToHexString(a.AsSpan(u * tight, unitSize)));
            Assert.Equal(
                Convert.ToHexString(source[u]),
                Convert.ToHexString(b.AsSpan(u * aligned16, unitSize)));
        }
    }

    [Fact]
    public void ReedSolomon_TooManyErasures_Fails()
    {
        const int k = 4, m = 2, unitSize = 32, stride = 32, total = k + m;
        var frame = new byte[total * stride];
        for (int u = 0; u < k; u++)
        {
            RandomNumberGenerator.Fill(frame.AsSpan(u * stride, unitSize));
        }
        CauchyReedSolomon.Encode(frame, unitSize, stride, k, m);

        // Lose 3 units with only m=2 parity — unrecoverable.
        var present = new bool[total];
        Array.Fill(present, true);
        foreach (int e in new[] { 0, 1, 2 })
        {
            present[e] = false;
        }

        Assert.False(CauchyReedSolomon.Decode(frame, unitSize, stride, k, m, present));
    }
}
