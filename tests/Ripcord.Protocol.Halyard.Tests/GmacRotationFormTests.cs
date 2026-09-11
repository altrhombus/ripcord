using System.Security.Cryptography;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Pins the <em>form</em> of the rotating GMAC key against the spec, independently recomputed.
///
/// <para><b>What this is and is not.</b> It is not a claim about the console — that was settled on the wire:
/// <c>ps5-remoteplay-v1-spec.md</c> §5.4 records the chained form GMAC-verifying 13670 of 13672 A/V packets
/// across <b>223 rotation windows</b>, the two misses being a torn capture tail, and marks it <b>[V]</b>. It
/// is a regression guard on a specific mistake this project already made once and could quietly make
/// again.</para>
///
/// <para><b>The mistake.</b> Window ≥ 1 re-folds <c>key0</c> — the already-folded window-0 GMAC key — with an
/// incrementing IV. An earlier implementation folded <c>aes_key</c> instead. Both are one plausible line of
/// code; both produce a correct-looking 16-byte key; both pass every test that only asks "does the key change
/// at the boundary and stay put inside a window", which is what the existing boundary tests ask. The wrong
/// one authenticated window 0 and rejected every packet after roughly the first half second of stream.</para>
///
/// <para>So this recomputes the spec's formula from its own words — fold, IV addition, the <c>0xAF6E</c>
/// stride — and compares. Two implementations of one written rule, which is the only thing a test without a
/// console can honestly check, and enough to catch a revert.</para>
///
/// <para>Also worth knowing, because it changes how alarming a bug here would be: an error in this is not
/// subtle. Key positions advance fast enough that a real session crosses windows continuously, so a wrong
/// rotation does not produce a rare glitch — it produces a stream that dies seconds in, every time. The
/// backlog described the symptom as "intermittent, very hard to diagnose"; the evidence says the opposite,
/// and a failure mode that loud is one of the reasons this gap was never the risk it looked like.</para>
/// </summary>
public class GmacRotationFormTests
{
    private static readonly byte[] AesKey = Convert.FromHexString("a1b2c3d4e5f60718293a4b5c6d7e8f90");
    private static readonly byte[] BaseIv = Convert.FromHexString("0f1e2d3c4b5a69788796a5b4c3d2e1f0");

    /// <summary>Windows worth checking: the first rotation, the second, one deep in the run the live capture
    /// covered, and the last window that capture reached.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(37)]
    [InlineData(222)]
    [InlineData(223)]
    public void TheRotatedKeyMatchesTheSpecFormula(long window)
    {
        using var crypto = new HalyardPacketCrypto(AesKey, BaseIv);

        // One buffer, reused: a stackalloc inside the loop grows the frame on every iteration (CA2014).
        Span<byte> actual = stackalloc byte[16];
        string expected = Hex.String(Expected(window));

        // Sample inside the window rather than only on its first unit: the whole window must share one key.
        foreach (ulong keyPos in Positions(window))
        {
            crypto.GmacKey(keyPos, actual);

            Assert.Equal(expected, Hex.String(actual.ToArray()));
        }
    }

    /// <summary>
    /// The specific wrong answer, asserted to be wrong.
    ///
    /// <para>A guard that only says "the key is what we compute" cannot fail if someone reintroduces the old
    /// form — both sides would move together. This names the superseded formula and requires it to disagree,
    /// so the revert has something to trip over.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(223)]
    public void FoldingTheAesKeyInsteadOfKey0_IsTheOldBugAndMustNotMatch(long window)
    {
        using var crypto = new HalyardPacketCrypto(AesKey, BaseIv);

        Span<byte> actual = stackalloc byte[16];
        crypto.GmacKey((ulong)(window * HalyardPacketCrypto.RotationWindow), actual);

        byte[] superseded = Fold(Concat(AesKey, IvAdd(BaseIv, window * HalyardPacketCrypto.RotationFactor)));

        Assert.NotEqual(Hex.String(superseded), Hex.String(actual.ToArray()));
    }

    [Fact]
    public void WindowZeroUsesTheFoldedBaseKeyDirectly()
    {
        using var crypto = new HalyardPacketCrypto(AesKey, BaseIv);

        Span<byte> actual = stackalloc byte[16];
        crypto.GmacKey(0, actual);

        Assert.Equal(Hex.String(Fold(Concat(AesKey, BaseIv))), Hex.String(actual.ToArray()));
    }

    private static IEnumerable<ulong> Positions(long window)
    {
        long start = window * HalyardPacketCrypto.RotationWindow;

        yield return (ulong)start;                                              // first unit of the window
        yield return (ulong)(start + 1);
        yield return (ulong)(start + HalyardPacketCrypto.RotationWindow / 2);
        yield return (ulong)(start + HalyardPacketCrypto.RotationWindow - 1);    // last unit before rotating
    }

    /// <summary>The spec's formula, written out from §5.4 rather than called into the implementation.</summary>
    private static byte[] Expected(long window)
    {
        byte[] key0 = Fold(Concat(AesKey, BaseIv));

        return window == 0
            ? key0
            : Fold(Concat(key0, IvAdd(BaseIv, window * HalyardPacketCrypto.RotationFactor)));
    }

    /// <summary><c>sha256_fold(x) = SHA-256(x)[0:16] XOR SHA-256(x)[16:32]</c>.</summary>
    private static byte[] Fold(byte[] input)
    {
        byte[] hash = SHA256.HashData(input);
        byte[] folded = new byte[16];

        for (int i = 0; i < 16; i++) folded[i] = (byte)(hash[i] ^ hash[i + 16]);

        return folded;
    }

    /// <summary><c>iv_add(iv, n)</c> — little-endian 128-bit addition, wrapping.</summary>
    private static byte[] IvAdd(byte[] iv, long n)
    {
        byte[] result = (byte[])iv.Clone();
        ulong carry = (ulong)n;

        for (int i = 0; i < result.Length && carry != 0; i++)
        {
            ulong sum = result[i] + (carry & 0xFF);
            result[i] = (byte)sum;
            carry >>= 8;
            carry += sum >> 8;
        }

        return result;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);

        return result;
    }
}
