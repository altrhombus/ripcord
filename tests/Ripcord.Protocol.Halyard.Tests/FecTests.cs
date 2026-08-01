using System.Security.Cryptography;
using Ripcord.Protocol.Halyard.Common.Streaming.Fec;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The video FEC math: GF(2^8) field laws and systematic Cauchy Reed-Solomon round-trips. These validate that
/// our decoder reconstructs exactly what the console's encoder produced (same field, same Cauchy matrix), so a
/// lost source unit is recovered rather than showing as a corrupt slice.
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
