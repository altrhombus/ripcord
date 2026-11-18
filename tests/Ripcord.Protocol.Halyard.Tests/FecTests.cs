using Ripcord.Protocol.Halyard.Common.Streaming.Fec;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The video FEC math: GF(2^8) field laws and systematic Cauchy Reed-Solomon round-trips.
///
/// <para><b>Scope — the round-trip tests are self-consistency tests, not ground truth.</b> They prove our
/// decoder inverts our encoder, over synthetic frames. Recovery from parity the console itself produced is
/// <see cref="FecRealFrameRecoveryTests"/>, which needs a private fixture.</para>
///
/// <para>What ties these tests to the console is first-party analysis of our own client, on both halves that
/// had to agree: the matrix form is [C]-confirmed by decompilation (see <see cref="CauchyReedSolomon"/>), and
/// the field it is built over is [V]-confirmed by a runtime dump of the client's inverse table on 2026-08-02
/// (<see cref="GaloisField256_MatchesConsoleInverseTable"/>). The round-trips run over that field and that
/// matrix.</para>
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
    public void ReedSolomon_RecoversMissingSourceUnits(int k, int m, int[] missing)
    {
        const int codedLength = 200;
        byte[] frame = EncodedFrame(k, m, codedLength, seed: k * 31 + m);
        byte[] pristine = (byte[])frame.Clone();

        bool[] arrived = Lose(frame, k + m, codedLength, missing);

        Assert.True(CauchyReedSolomon.RecoverSource(frame, codedLength, k, m, arrived));
        AssertSourceMatches(pristine, frame, k, codedLength);
    }

    [Fact]
    public void ReedSolomon_LosingAParityUnitStillRecoversSource()
    {
        const int k = 4, m = 2, codedLength = 64;
        byte[] frame = EncodedFrame(k, m, codedLength, seed: 7);
        byte[] pristine = (byte[])frame.Clone();

        // Lose one source unit and parity unit 0: parity unit 1 is enough for one missing source unit.
        bool[] arrived = Lose(frame, k + m, codedLength, [1, k + 0]);

        Assert.True(CauchyReedSolomon.RecoverSource(frame, codedLength, k, m, arrived));
        AssertSourceMatches(pristine, frame, k, codedLength);
    }

    /// <summary>
    /// Every loss pattern that leaves enough parity is recovered, whichever parity units survive. Any square
    /// submatrix of a Cauchy matrix is invertible, so no pattern should fail; this walks all of them for a
    /// small frame.
    /// </summary>
    [Fact]
    public void ReedSolomon_RecoversEveryLossPatternThatLeavesEnoughParity()
    {
        const int k = 5, m = 3, codedLength = 12;
        byte[] original = EncodedFrame(k, m, codedLength, seed: 99);
        int recovered = 0;

        for (int mask = 0; mask < 1 << (k + m); mask++)
        {
            int lostSource = 0, lostParity = 0;
            for (int u = 0; u < k + m; u++)
            {
                if ((mask & (1 << u)) == 0) continue;
                if (u < k) lostSource++; else lostParity++;
            }

            byte[] frame = (byte[])original.Clone();
            bool[] arrived = Lose(frame, k + m, codedLength,
                Enumerable.Range(0, k + m).Where(u => (mask & (1 << u)) != 0).ToArray());

            bool enough = lostSource <= m - lostParity;
            Assert.Equal(enough, CauchyReedSolomon.RecoverSource(frame, codedLength, k, m, arrived));
            if (enough)
            {
                AssertSourceMatches(original, frame, k, codedLength);
                recovered++;
            }
        }

        Assert.True(recovered > 0);
    }

    /// <summary>
    /// The coded length is whatever the console coded over (a multiple of 4, spec §6.2), and units sit
    /// back to back at that spacing. A real cap47 length that is 4-aligned but not 16-aligned recovers.
    /// </summary>
    [Theory]
    [InlineData(6, 3, 1004)]
    [InlineData(6, 3, 1012)]
    public void ReedSolomon_RecoversAtARealCodedLength(int k, int m, int codedLength)
    {
        byte[] frame = EncodedFrame(k, m, codedLength, seed: codedLength);
        byte[] pristine = (byte[])frame.Clone();

        bool[] arrived = Lose(frame, k + m, codedLength, [0, k - 1]);

        Assert.True(CauchyReedSolomon.RecoverSource(frame, codedLength, k, m, arrived));
        AssertSourceMatches(pristine, frame, k, codedLength);
    }

    [Fact]
    public void ReedSolomon_TooManyLosses_Fails()
    {
        const int k = 4, m = 2, codedLength = 32;
        byte[] frame = EncodedFrame(k, m, codedLength, seed: 3);

        // Three source units lost with only two parity units: unrecoverable.
        bool[] arrived = Lose(frame, k + m, codedLength, [0, 1, 2]);

        Assert.False(CauchyReedSolomon.RecoverSource(frame, codedLength, k, m, arrived));
    }

    [Fact]
    public void ReedSolomon_NoParity_RecoversNothingButAcceptsACompleteFrame()
    {
        const int k = 3, codedLength = 16;
        byte[] frame = EncodedFrame(k, 0, codedLength, seed: 5);

        Assert.True(CauchyReedSolomon.RecoverSource(frame, codedLength, k, 0, [true, true, true]));
        Assert.False(CauchyReedSolomon.RecoverSource(frame, codedLength, k, 0, [true, false, true]));
    }

    private static byte[] EncodedFrame(int k, int m, int codedLength, int seed)
    {
        var frame = new byte[(k + m) * codedLength];
        new Random(seed).NextBytes(frame.AsSpan(0, k * codedLength));
        CauchyReedSolomon.Encode(frame, codedLength, k, m);
        return frame;
    }

    private static bool[] Lose(byte[] frame, int units, int codedLength, int[] lost)
    {
        var arrived = new bool[units];
        Array.Fill(arrived, true);
        foreach (int u in lost)
        {
            Array.Clear(frame, u * codedLength, codedLength);
            arrived[u] = false;
        }

        return arrived;
    }

    private static void AssertSourceMatches(byte[] expected, byte[] actual, int k, int codedLength)
    {
        for (int u = 0; u < k; u++)
        {
            Assert.Equal(
                Convert.ToHexString(expected.AsSpan(u * codedLength, codedLength)),
                Convert.ToHexString(actual.AsSpan(u * codedLength, codedLength)));
        }
    }
}