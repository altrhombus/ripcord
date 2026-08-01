using Ripcord.Core.Net.Crypto;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Anchors the CFB128 / OFB / CTR keystream modes against the public NIST SP 800-38A Appendix F test
/// vectors for AES-128. Establishing that our modes match the standard means the higher protocol layers
/// that build on them (control field crypto, streaminfo, per-packet cipher) rest on verified primitives.
/// </summary>
public class AesKeystreamModesTests
{
    // SP 800-38A Appendix F common inputs.
    private static readonly byte[] Key = Hex.Bytes("2b7e151628aed2a6abf7158809cf4f3c");
    private static readonly byte[] Iv = Hex.Bytes("000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] Plaintext = Hex.Bytes(
        "6bc1bee22e409f96e93d7e117393172a" +
        "ae2d8a571e03ac9c9eb76fac45af8e51" +
        "30c81c46a35ce411e5fbc1191a0a52ef" +
        "f69f2445df4f9b17ad2b417be66c3710");

    [Fact]
    public void Cfb128_MatchesNistVector()
    {
        // SP 800-38A F.3.13 (CFB128-AES128.Encrypt).
        var expected = Hex.Bytes(
            "3b3fd92eb72dad20333449f8e83cfb4a" +
            "c8a64537a0b3a93fcde3cdad9f1ce58b" +
            "26751f67a3cbb140b1808cf187a4f4df" +
            "c04b05357c5d1c0eeac4c66f9ff7f2e6");

        var ct = AesKeystreamModes.Cfb128(Key, Iv, Plaintext, encrypt: true);
        Assert.Equal(Hex.String(expected), Hex.String(ct));

        var pt = AesKeystreamModes.Cfb128(Key, Iv, ct, encrypt: false);
        Assert.Equal(Hex.String(Plaintext), Hex.String(pt));
    }

    [Fact]
    public void Cfb128_HandlesPartialFinalBlock()
    {
        // A partial (8-byte) final segment must be a stream XOR of the truncated keystream — the case the
        // control field cipher hits for RP-OSType ("Win10.0\0").
        var partial = Plaintext.AsSpan(0, 8).ToArray();
        var ct = AesKeystreamModes.Cfb128(Key, Iv, partial, encrypt: true);
        Assert.Equal(8, ct.Length);
        // First 8 bytes of the full-block ciphertext must match (feedback only changes after a full block).
        var full = AesKeystreamModes.Cfb128(Key, Iv, Plaintext, encrypt: true);
        Assert.Equal(Hex.String(full.AsSpan(0, 8)), Hex.String(ct));
        Assert.Equal(Hex.String(partial), Hex.String(AesKeystreamModes.Cfb128(Key, Iv, ct, encrypt: false)));
    }

    [Fact]
    public void Ofb_MatchesNistVector()
    {
        // SP 800-38A F.4.1 (OFB-AES128.Encrypt).
        var expected = Hex.Bytes(
            "3b3fd92eb72dad20333449f8e83cfb4a" +
            "7789508d16918f03f53c52dac54ed825" +
            "9740051e9c5fecf64344f7a82260edcc" +
            "304c6528f659c77866a510d9c1d6ae5e");

        var ct = AesKeystreamModes.Ofb(Key, Iv, Plaintext);
        Assert.Equal(Hex.String(expected), Hex.String(ct));
        Assert.Equal(Hex.String(Plaintext), Hex.String(AesKeystreamModes.Ofb(Key, Iv, ct)));
    }

    [Fact]
    public void Ctr_MatchesNistVector()
    {
        // SP 800-38A F.5.1 (CTR-AES128.Encrypt) — initial counter block below.
        var counter = Hex.Bytes("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var expected = Hex.Bytes(
            "874d6191b620e3261bef6864990db6ce" +
            "9806f66b7970fdff8617187bb9fffdff" +
            "5ae4df3edbd5d35e5b4f09020db03eab" +
            "1e031dda2fbe03d1792170a0f3009cee");

        var ct = AesKeystreamModes.Ctr(Key, counter, Plaintext);
        Assert.Equal(Hex.String(expected), Hex.String(ct));
        Assert.Equal(Hex.String(Plaintext), Hex.String(AesKeystreamModes.Ctr(Key, counter, ct)));
    }

    [Fact]
    public void Ctr_CounterIncrementsAcrossFullWidth()
    {
        // A counter at the 32-bit boundary must carry into the next byte (full 128-bit increment).
        var counter = Hex.Bytes("000000000000000000000000ffffffff");
        AesKeystreamModes.IncrementBigEndian(counter);
        Assert.Equal("00000000000000000000000100000000", Hex.String(counter));
    }
}
