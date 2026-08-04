using System.Buffers.Binary;
using System.Security.Cryptography;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Self-contained tests for the v1 control-plane crypto construction: the per-field IV formula, the
/// context-key selection rule, and the CFB/OFB field/streaminfo round-trips. These use synthetic keys so
/// they carry no extracted secrets and always run. Validation against the real captured KDF/IV/RP-Auth
/// ground truth lives in <see cref="LiveControlVectorTests"/>, which self-skips when the dirty-room
/// fixture is absent.
/// </summary>
public class ControlCryptoTests
{
    [Fact]
    public void FieldIv_MatchesExplicitHmacConstruction()
    {
        var contextKey = Hex.Bytes("0a1b2c3d4e5f60718293a4b5c6d7e8f9"); // shape only; value is arbitrary here
        var material = Hex.Bytes("3a4b5c6d7e8f9001a2b3c4d5e6f70819");
        const ulong counter = 7;

        var iv = HalyardFieldIv.Derive(contextKey, material, counter);

        var msg = new byte[16 + 8];
        material.CopyTo(msg, 0);
        BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(16), counter);
        var expected = HMACSHA256.HashData(contextKey, msg).AsSpan(0, 16).ToArray();

        Assert.Equal(16, iv.Length);
        Assert.Equal(Hex.String(expected), Hex.String(iv));
    }

    [Fact]
    public void ContextKeySelection_FollowsRule()
    {
        var keys = new HalyardFieldContextKeys(
            codecInHigh: Hex.Bytes("0b1c2d3e4f5a6b7c8d9eafbec0d1e2f3"),
            selectorOne: Hex.Bytes("0a1b2c3d4e5f60718293a4b5c6d7e8f9"),
            selectorZero: Hex.Bytes("0c1d2e3f405162738495a6b7c8d9eafb"),
            fallbackZero: Hex.Bytes("00000000000000000000000000000000"));

        // Codec high band wins regardless of version selector.
        Assert.Equal(Hex.String(keys.CodecInHigh.Span), Hex.String(keys.Select(8, 1).Span));
        Assert.Equal(Hex.String(keys.CodecInHigh.Span), Hex.String(keys.Select(9, 0).Span));
        // Otherwise the version selector decides.
        Assert.Equal(Hex.String(keys.SelectorOne.Span), Hex.String(keys.Select(2, 1).Span));
        Assert.Equal(Hex.String(keys.SelectorZero.Span), Hex.String(keys.Select(2, 0).Span));
        Assert.Equal(Hex.String(keys.FallbackZero.Span), Hex.String(keys.Select(2, 2).Span));
    }

    [Fact]
    public void Kdf_Ps4Variant_MatchesFormulaAndDiffersFromPs5()
    {
        // Synthetic tables (arbitrary bytes — no extracted material) so the PS4 (mode-0) arithmetic and its
        // dispatch are exercised in isolation. Both PS5 and PS4 pairs are supplied so either mode can run.
        byte[] ps5Table1 = Pattern(0x10), ps5Table2 = Pattern(0x40);
        byte[] ps4Table1 = Pattern(0x80), ps4Table2 = Pattern(0xC0);
        var secrets = new HalyardControlSecrets(
            ps5Table1, ps5Table2,
            new HalyardFieldContextKeys(new byte[16], new byte[16], new byte[16], new byte[16]),
            ps4Table1, ps4Table2);
        var kdf = new HalyardControlKdf(secrets);

        byte[] nonce = Hex.Bytes("101112131415161718191a1b1c1d1e1f");
        byte[] companion = Hex.Bytes("202122232425262728292a2b2c2d2e2f");

        // Explicit re-derivation of the mode-0 formula (the vendor control DLL, FUN_1fdd80): the key is
        // companion-derived (table1 by nonce[7]>>3), the material nonce-derived (table2 by nonce[0]>>3).
        var t1 = secrets.Ps4KdfTable1Entry(nonce[7] >> 3);
        var t2 = secrets.Ps4KdfTable2Entry(nonce[0] >> 3);
        var expKey = new byte[16];
        var expMat = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            expKey[i] = (byte)((((t1[i] ^ companion[i]) + 0x21 + i) & 0xFF) ^ nonce[i]);
            expMat[i] = (byte)(((nonce[i] + 0x36 + i) & 0xFF) ^ t2[i]);
        }

        var (key, material) = kdf.Derive(nonce, companion, versionSelector: 0);
        Assert.Equal(Hex.String(expKey), Hex.String(key));
        Assert.Equal(Hex.String(expMat), Hex.String(material));

        // The mode-0 variant must actually differ from the PS5 (mode-1) path over the same inputs.
        var (ps5Key, ps5Mat) = kdf.Derive(nonce, companion, versionSelector: 1);
        Assert.NotEqual(Hex.String(key), Hex.String(ps5Key));
        Assert.NotEqual(Hex.String(material), Hex.String(ps5Mat));
    }

    [Fact]
    public void Kdf_Ps4Variant_ThrowsWhenTablesAbsent()
    {
        // A PS5-only secrets object must fail loudly if asked for the PS4 variant, not silently mis-key.
        var secrets = new HalyardControlSecrets(
            Pattern(0x10), Pattern(0x40),
            new HalyardFieldContextKeys(new byte[16], new byte[16], new byte[16], new byte[16]));
        var kdf = new HalyardControlKdf(secrets);
        Assert.False(secrets.HasPs4Tables);
        Assert.Throws<InvalidOperationException>(() => kdf.Derive(new byte[16], new byte[16], versionSelector: 0));
    }

    private static byte[] Pattern(int seed)
    {
        var t = new byte[HalyardControlSecrets.KdfTableLength];
        for (int i = 0; i < t.Length; i++) t[i] = (byte)(seed + i);
        return t;
    }

    [Fact]
    public void FieldCipher_Cfb_RoundTripsAtDifferentCounters()
    {
        var key = Hex.Bytes("4a5b6c7d8e9f0011223344556677889a");
        var material = Hex.Bytes("5a6b7c8d9e0f1122334455667788990a");
        var contextKey = Hex.Bytes("0a1b2c3d4e5f60718293a4b5c6d7e8f9");
        var crypto = new HalyardControlFieldCrypto(key, material, contextKey);

        // 16-byte field (RP-Auth shape) and 8-byte field (RP-OSType shape) at their field counters.
        var auth = Hex.Bytes("6162636431323334") .Concat(new byte[8]).ToArray();
        var ct0 = crypto.EncryptField(0, auth);
        Assert.Equal(Hex.String(auth), Hex.String(crypto.DecryptField(0, ct0)));

        var os = Hex.Bytes("57696e31302e3000");
        var ct2 = crypto.EncryptField(2, os);
        Assert.Equal(8, ct2.Length);
        Assert.Equal(Hex.String(os), Hex.String(crypto.DecryptField(2, ct2)));

        // Base64 helpers agree with the raw path.
        Assert.Equal(Convert.ToBase64String(ct0), crypto.EncryptFieldBase64(0, auth));
    }

    [Fact]
    public void Streaminfo_Ofb_IsSymmetric()
    {
        var key = Hex.Bytes("6a7b8c9d0e1f2233445566778899aabb");
        var material = Hex.Bytes("00112233445566778899aabbccddeeff");
        var contextKey = Hex.Bytes("0a1b2c3d4e5f60718293a4b5c6d7e8f9");
        var crypto = new HalyardControlFieldCrypto(key, material, contextKey);

        var plain = System.Text.Encoding.ASCII.GetBytes("{\"sessionId\":\"sessionId4321\",\"streamResolutions\":[{}]}");
        var ct = crypto.StreaminfoCrypt(0, plain);
        Assert.NotEqual(Hex.String(plain), Hex.String(ct));
        Assert.Equal(Hex.String(plain), Hex.String(crypto.StreaminfoCrypt(0, ct)));
    }
}
