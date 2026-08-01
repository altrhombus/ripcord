using System.Security.Cryptography;
using Ripcord.Core.Net.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Guards the A/V hot-path optimisations: a cached payload block cipher and an allocation-free 128-bit IV add.
///
/// <para>
/// The risk being guarded is silent and delayed. The payload AES key is fixed for a session, but the GMAC key
/// <em>rotates</em> every <see cref="HalyardPacketCrypto.RotationWindow"/> key positions — so a caching mistake
/// would not fail on the bench, it would fail minutes into a session at the first rotation boundary, as a total
/// authentication failure (every packet dropped, black screen). And because UDP reorders, key positions arrive
/// non-monotonically, so packets legitimately alternate across a boundary.
/// </para>
/// </summary>
public class PacketCryptoHotPathTests
{
    private static readonly byte[] AesKey = Convert.FromHexString("1a2b3c4ddeadbeef0011223344556677");
    private static readonly byte[] BaseIv = Convert.FromHexString("0102030405060708090a0b0c0d0e0f10");

    private static HalyardPacketCrypto NewCrypto() => new(AesKey, BaseIv);

    // ---- payload cipher: cached AES must equal a freshly-created one ----

    [Theory]
    [InlineData(0UL)]
    [InlineData(16UL)]
    [InlineData(45_000UL * 16)]          // at a rotation boundary
    [InlineData(45_000UL * 16 + 1_000)]  // just past one
    [InlineData(ulong.MaxValue / 2)]
    public void CryptPayload_MatchesTheUncachedReference(ulong keyPos)
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(1_400);

        // Reference: exactly what the old code did — a fresh Aes per call, via the allocating overload.
        Span<byte> nonce = stackalloc byte[HalyardPacketCrypto.BlockSize];
        using (var reference = NewCrypto())
        {
            reference.CtrNonce(keyPos, nonce);
        }

        byte[] expected = AesKeystreamModes.Ctr(AesKey, nonce, plaintext, littleEndianCounter: true);

        using var crypto = NewCrypto();
        byte[] actual = plaintext.ToArray();
        crypto.CryptPayloadInPlace(keyPos, actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CryptPayloadInPlace_IsSymmetricAndReusableAcrossManyPackets()
    {
        // The whole point of the cache: the same instance serves thousands of packets. If any per-call state
        // leaked between them, the round trip would break somewhere in this loop.
        using var crypto = NewCrypto();

        for (int i = 0; i < 2_000; i++)
        {
            ulong keyPos = (ulong)i * 1_360;
            byte[] original = RandomNumberGenerator.GetBytes(64 + (i % 900));
            byte[] buffer = original.ToArray();

            crypto.CryptPayloadInPlace(keyPos, buffer);
            Assert.NotEqual(original, buffer);          // actually encrypted

            crypto.CryptPayloadInPlace(keyPos, buffer);
            Assert.Equal(original, buffer);             // and symmetric
        }
    }

    [Fact]
    public void CryptPayload_AllocatingOverload_StillAgreesWithInPlace()
    {
        using var crypto = NewCrypto();
        byte[] plaintext = RandomNumberGenerator.GetBytes(512);

        byte[] viaAlloc = crypto.CryptPayload(1_234_567, plaintext);
        byte[] viaInPlace = plaintext.ToArray();
        crypto.CryptPayloadInPlace(1_234_567, viaInPlace);

        Assert.Equal(viaAlloc, viaInPlace);
    }

    // ---- GMAC key rotation: deliberately NOT cached, and must stay correct across boundaries ----

    [Fact]
    public void GmacKey_ChangesAtEveryRotationBoundary()
    {
        using var crypto = NewCrypto();
        const ulong window = (ulong)HalyardPacketCrypto.RotationWindow;

        byte[] KeyAt(ulong keyPos)
        {
            var k = new byte[HalyardPacketCrypto.BlockSize];
            crypto.GmacKey(keyPos, k);
            return k;
        }

        // Within a window the key is stable; crossing a boundary it must change.
        Assert.Equal(KeyAt(0), KeyAt(window - 1));
        Assert.NotEqual(KeyAt(window - 1), KeyAt(window));
        Assert.Equal(KeyAt(window), KeyAt(window + 1));
        Assert.NotEqual(KeyAt(window), KeyAt(window * 2));
    }

    [Fact]
    public void GmacKey_IsStableWhenKeyPositionsArriveOutOfOrderAcrossABoundary()
    {
        // UDP reorders, so around a boundary consecutive packets alternate between window n and n+1. Whatever
        // caching strategy is used, the key for a given key position must never depend on what preceded it.
        using var crypto = NewCrypto();
        const ulong window = (ulong)HalyardPacketCrypto.RotationWindow;

        byte[] KeyAt(ulong keyPos)
        {
            var k = new byte[HalyardPacketCrypto.BlockSize];
            crypto.GmacKey(keyPos, k);
            return k;
        }

        byte[] beforeFirst = KeyAt(window - 1);
        byte[] afterFirst = KeyAt(window);

        // Alternate back and forth across the boundary several times.
        foreach (ulong keyPos in (ulong[])[window, window - 1, window + 5, window - 3, window * 2, window - 1])
        {
            byte[] expected = keyPos < window ? beforeFirst : KeyAt(keyPos);
            Assert.Equal(expected, KeyAt(keyPos));
        }

        // And the two originals still reproduce exactly.
        Assert.Equal(beforeFirst, KeyAt(window - 1));
        Assert.Equal(afterFirst, KeyAt(window));
    }

    [Fact]
    public void VerifyPacket_RoundTripsAcrossARotationBoundary()
    {
        // End-to-end: seal and verify on either side of a boundary with one long-lived crypto instance.
        using var crypto = NewCrypto();
        const ulong window = (ulong)HalyardPacketCrypto.RotationWindow;

        foreach (ulong keyPos in (ulong[])[0, window - 16, window, window + 16, window * 3])
        {
            byte[] packet = RandomNumberGenerator.GetBytes(200);
            byte[] sealed_ = crypto.SealPacket(keyPos, packet);

            Assert.True(crypto.VerifyPacket(keyPos, sealed_), $"tag should verify at keyPos {keyPos}");

            // A tag from the wrong window must not verify — proves the rotation is actually in play.
            Assert.False(
                crypto.VerifyPacket(keyPos + window, sealed_),
                $"a tag from keyPos {keyPos} must not verify one window later");
        }
    }

    // ---- IvAddFast must match the BigInteger reference exactly ----

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(0xFFUL)]
    [InlineData(ulong.MaxValue)]
    public void IvAddFast_MatchesBigIntegerReference_ForKnownAddends(ulong addend)
    {
        AssertIvAddAgrees(BaseIv, addend);
    }

    [Fact]
    public void IvAddFast_MatchesBigIntegerReference_AcrossTheCarryBoundary()
    {
        // The interesting case for a two-ulong implementation: the low half wrapping into the high half.
        byte[] iv = new byte[16];
        iv[0] = 0xFF; iv[1] = 0xFF; iv[2] = 0xFF; iv[3] = 0xFF;
        iv[4] = 0xFF; iv[5] = 0xFF; iv[6] = 0xFF; iv[7] = 0xFF; // low half all ones

        foreach (ulong addend in (ulong[])[1, 2, 0xFF, ulong.MaxValue])
        {
            AssertIvAddAgrees(iv, addend);
        }
    }

    [Fact]
    public void IvAddFast_MatchesBigIntegerReference_WhenTheWholeIvWraps()
    {
        // All-ones IV: adding anything wraps modulo 2^128, which the high half must do silently.
        byte[] iv = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        foreach (ulong addend in (ulong[])[1, 42, ulong.MaxValue])
        {
            AssertIvAddAgrees(iv, addend);
        }
    }

    [Fact]
    public void IvAddFast_MatchesBigIntegerReference_OverRandomInputs()
    {
        for (int i = 0; i < 500; i++)
        {
            byte[] iv = RandomNumberGenerator.GetBytes(16);
            ulong addend = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
            AssertIvAddAgrees(iv, addend);
        }
    }

    private static void AssertIvAddAgrees(byte[] iv, ulong addend)
    {
        var viaBigInteger = new byte[16];
        HalyardPacketCrypto.IvAdd(iv, addend, viaBigInteger);

        var viaFast = new byte[16];
        HalyardPacketCrypto.IvAddFast(iv, addend, viaFast);

        Assert.Equal(
            Convert.ToHexString(viaBigInteger),
            Convert.ToHexString(viaFast));
    }

    [Fact]
    public void Nonces_AreUnchangedByTheFastPath()
    {
        // GmacNonce/CtrNonce switched to IvAddFast; their outputs must be identical to the BigInteger form,
        // because a changed nonce silently breaks interop with the console rather than failing loudly here.
        using var crypto = NewCrypto();

        // Allocated once outside the loop (CA2014): stackalloc inside a loop accumulates a frame per iteration.
        Span<byte> gmac = stackalloc byte[16];
        Span<byte> ctr = stackalloc byte[16];

        foreach (ulong keyPos in (ulong[])[0, 16, 1_360, 45_000, 45_000UL * 16, ulong.MaxValue / 4])
        {
            crypto.GmacNonce(keyPos, gmac);
            crypto.CtrNonce(keyPos, ctr);

            var expectedGmac = new byte[16];
            var expectedCtr = new byte[16];
            HalyardPacketCrypto.IvAdd(BaseIv, keyPos >> 4, expectedGmac);
            HalyardPacketCrypto.IvAdd(BaseIv, (keyPos + 16) >> 4, expectedCtr);

            Assert.Equal(Convert.ToHexString(expectedGmac), Convert.ToHexString(gmac));
            Assert.Equal(Convert.ToHexString(expectedCtr), Convert.ToHexString(ctr));
        }
    }
}
