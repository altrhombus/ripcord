using System.Security.Cryptography;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The v1 stream-crypto known-answer / round-trip suite — the C# counterpart of the reference
/// <c>stream_crypto_reimpl.py</c> self-test. The layer is now [V] end-to-end (the key derivation reproduces a
/// real console's keys byte-for-byte — see <c>LiveStreamKeyVectorTests</c>); these self-contained tests pin
/// every step with synthetic keypairs: ecdhSignature construction and mutual verification, ECDH-P256 agreement,
/// per-direction key derivation, the CTR/GMAC nonce relationship, encrypt-then-MAC round-trips,
/// tamper-detection, and the GMAC-key rotation boundary. They are fully self-contained (synthetic
/// keypairs) and require no secrets.
/// </summary>
public class StreamCryptoTests
{
    private static readonly byte[] HandshakeKey = Hex.Bytes("7a8b9c0d1e2f3344556677889900aabb");

    [Fact]
    public void EcdhSignature_IsHmacSha256OverPubkey_AndVerifies()
    {
        var (kp, pub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP256);
        using (kp)
        {
            Assert.Equal(65, pub.Length);
            Assert.Equal(0x04, pub[0]);

            var sig = HalyardStreamKeySchedule.ComputeEcdhSignature(HandshakeKey, pub);
            Assert.Equal(Hex.String(HMACSHA256.HashData(HandshakeKey, pub)), Hex.String(sig));
            Assert.True(HalyardStreamKeySchedule.VerifyEcdhSignature(HandshakeKey, pub, sig));

            sig[0] ^= 0x01;
            Assert.False(HalyardStreamKeySchedule.VerifyEcdhSignature(HandshakeKey, pub, sig));
        }
    }

    [Fact]
    public void EcdhP256_SharedSecretAgrees_AndPerDirectionKeysDiffer()
    {
        var (clientKp, clientPub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP256);
        var (serverKp, serverPub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP256);
        using (clientKp)
        using (serverKp)
        {
            var ssClient = HalyardStreamKeySchedule.DeriveSharedSecret(clientKp, serverPub);
            var ssServer = HalyardStreamKeySchedule.DeriveSharedSecret(serverKp, clientPub);
            Assert.Equal(32, ssClient.Length);
            Assert.Equal(Hex.String(ssClient), Hex.String(ssServer));

            var c2s = HalyardStreamKeySchedule.DeriveDirection(ssClient, HandshakeKey, HalyardStreamKeySchedule.DirectionClientToServer);
            var s2c = HalyardStreamKeySchedule.DeriveDirection(ssClient, HandshakeKey, HalyardStreamKeySchedule.DirectionServerToClient);
            Assert.NotEqual(Hex.String(c2s.AesKey), Hex.String(s2c.AesKey));
            Assert.NotEqual(Hex.String(c2s.BaseIv), Hex.String(s2c.BaseIv));
        }
    }

    [Theory]
    [InlineData(0x0d)]
    [InlineData(0x0e)]
    [InlineData(0x10)]
    [InlineData(17)] // 0x11 — the version our live capture negotiated
    public void CurveForVersion_AnswersP521_ForTheVersionsWeHaveObserved(int version)
        => Assert.Equal(HalyardStreamCurve.NistP521, HalyardStreamKeySchedule.CurveForVersion(version));

    /// <summary>
    /// Outside the observed range this must refuse rather than answer.
    ///
    /// <para>These cases asserted <c>NistP256</c> until 2026-09-11, which made a retracted guess look like
    /// settled behaviour with a test behind it. The spec withdrew that default explicitly - "the earlier
    /// 'v1 uses P-256, the default' was a guess" - and only the P-521 range was ever confirmed. A test that
    /// pins a guess is worse than no test: it converts an open question into a documented answer and stops
    /// the next person asking.</para>
    ///
    /// <para>0x0c and 0x12 are the two that matter, being one step outside each end of the range, which is
    /// where an off-by-one in the bound would show.</para>
    /// </summary>
    [Theory]
    [InlineData(0)]     // the old default argument, which used to land silently on the guess
    [InlineData(9)]
    [InlineData(0x0c)]  // one below the validated range
    [InlineData(0x12)]  // one above it
    [InlineData(0xff)]
    public void CurveForVersion_RefusesAVersionWhoseCurveWeHaveNeverSeen(int version)
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => HalyardStreamKeySchedule.CurveForVersion(version));

        // The message has to name the version: the symptom this replaces is an unexplained session
        // rejection, so the exception earns its place only by saying what could not be answered.
        Assert.Contains(version.ToString(), ex.Message);
    }

    [Fact]
    public void EcdhP521_133BytePubkeys_AgreeAndDeriveMatchingKeys()
    {
        var (clientKp, clientPub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        var (serverKp, serverPub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        using (clientKp)
        using (serverKp)
        {
            Assert.Equal(133, clientPub.Length);
            Assert.Equal(0x04, clientPub[0]);

            var ssClient = HalyardStreamKeySchedule.DeriveSharedSecret(clientKp, serverPub);
            var ssServer = HalyardStreamKeySchedule.DeriveSharedSecret(serverKp, clientPub);
            Assert.Equal(66, ssClient.Length); // P-521 X coordinate
            Assert.Equal(Hex.String(ssClient), Hex.String(ssServer));

            // The 66-byte X feeds the same per-direction KDF; both directions differ.
            var c2s = HalyardStreamKeySchedule.DeriveDirection(ssClient, HandshakeKey, HalyardStreamKeySchedule.DirectionClientToServer);
            var s2c = HalyardStreamKeySchedule.DeriveDirection(ssClient, HandshakeKey, HalyardStreamKeySchedule.DirectionServerToClient);
            Assert.NotEqual(Hex.String(c2s.AesKey), Hex.String(s2c.AesKey));
        }
    }

    [Fact]
    public void DeriveDirection_MatchesExplicitSp800108Construction()
    {
        var sharedSecret = Hex.Bytes("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");
        var keys = HalyardStreamKeySchedule.DeriveDirection(sharedSecret, HandshakeKey, HalyardStreamKeySchedule.DirectionClientToServer);

        // info = 01 || dir || 00 || handshakeKey(16) || 01 00  (0x0100 = 256 output bits; live-validated)
        var info = new byte[21];
        info[0] = 0x01;
        info[1] = HalyardStreamKeySchedule.DirectionClientToServer;
        info[2] = 0x00;
        HandshakeKey.CopyTo(info, 3);
        info[19] = 0x01;
        info[20] = 0x00;
        var block = HMACSHA256.HashData(sharedSecret, info);

        Assert.Equal(Hex.String(block.AsSpan(0, 16)), Hex.String(keys.AesKey));
        Assert.Equal(Hex.String(block.AsSpan(16, 16)), Hex.String(keys.BaseIv));
    }

    [Fact]
    public void CtrNonce_IsOneBlockAboveGmacNonce()
    {
        var keys = new HalyardStreamKeySchedule.DirectionKeys(
            Hex.Bytes("000102030405060708090a0b0c0d0e0f"),
            Hex.Bytes("ffffffffffffffff0000000000000000"));
        var crypto = new HalyardPacketCrypto(keys);

        Span<byte> gmac = stackalloc byte[16];
        Span<byte> ctr = stackalloc byte[16];
        foreach (ulong keyPos in new ulong[] { 0, 0x400, 0x00013060, 45000, 123456789 })
        {
            crypto.GmacNonce(keyPos, gmac);
            crypto.CtrNonce(keyPos, ctr);

            // ctr == gmac + 1 (little-endian)
            var g = new System.Numerics.BigInteger(gmac, isUnsigned: true, isBigEndian: false);
            var c = new System.Numerics.BigInteger(ctr, isUnsigned: true, isBigEndian: false);
            Assert.Equal((g + 1) & ((System.Numerics.BigInteger.One << 128) - 1), c);
        }
    }

    [Fact]
    public void PerPacket_EncryptThenMac_RoundTripsAndDetectsTamper()
    {
        var (clientKp, clientPub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP256);
        var (serverKp, serverPub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP256);
        using (clientKp)
        using (serverKp)
        {
            var shared = HalyardStreamKeySchedule.DeriveSharedSecret(clientKp, serverPub);
            _ = serverPub;
            var c2s = HalyardStreamKeySchedule.DeriveDirection(shared, HandshakeKey, HalyardStreamKeySchedule.DirectionClientToServer);
            var crypto = new HalyardPacketCrypto(c2s);

            foreach ((int seq, ulong kp) in new (int, ulong)[] { (0x4b, 0x00013060), (0x4c, 0x00013460), (1, 44999), (2, 45000), (3, 123456789) })
            {
                var plain = System.Text.Encoding.ASCII.GetBytes(new string('N', 176)); // NALU-like payload
                var cipher = crypto.CryptPayload(kp, plain);
                Assert.NotEqual(Hex.String(plain), Hex.String(cipher)); // actually encrypted

                var packet = MakeAvPacket(seq, kp, cipher);
                var sealed_ = crypto.SealPacket(kp, packet);

                Assert.True(crypto.VerifyPacket(kp, sealed_));

                var tampered = (byte[])sealed_.Clone();
                tampered[^1] ^= 0x01;
                Assert.False(crypto.VerifyPacket(kp, tampered));

                var recovered = crypto.CryptPayload(kp, sealed_.AsSpan(18).ToArray());
                Assert.Equal(Hex.String(plain), Hex.String(recovered));
            }
        }
    }

    [Fact]
    public void GmacKey_RotatesOnWindowBoundary_StableWithin()
    {
        var crypto = new HalyardPacketCrypto(
            Hex.Bytes("101112131415161718191a1b1c1d1e1f"),
            Hex.Bytes("202122232425262728292a2b2c2d2e2f"));

        Span<byte> k44999 = stackalloc byte[16];
        Span<byte> k45000 = stackalloc byte[16];
        Span<byte> k89999 = stackalloc byte[16];
        crypto.GmacKey(44999, k44999);
        crypto.GmacKey(45000, k45000);
        crypto.GmacKey(89999, k89999);

        Assert.NotEqual(Hex.String(k44999), Hex.String(k45000)); // rotates across the boundary
        Assert.Equal(Hex.String(k45000), Hex.String(k89999));    // stable within a window
    }

    // [type][seq u16][sub-hdr 7][tag@10 placeholder][keypos@14 u32 BE][payload]
    private static byte[] MakeAvPacket(int seq, ulong keyPos, byte[] payload)
    {
        var packet = new byte[18 + payload.Length];
        packet[0] = 0x02;
        packet[1] = (byte)(seq >> 8);
        packet[2] = (byte)seq;
        // off 3..9 sub-header (arbitrary but fixed), off 10..13 zero tag placeholder
        packet[3] = 0x00; packet[4] = 0x02; packet[5] = 0x00; packet[6] = 0x00;
        packet[7] = 0x1c; packet[8] = 0x01; packet[9] = 0x03;
        packet[14] = (byte)(keyPos >> 24);
        packet[15] = (byte)(keyPos >> 16);
        packet[16] = (byte)(keyPos >> 8);
        packet[17] = (byte)keyPos;
        payload.CopyTo(packet, 18);
        return packet;
    }
}
