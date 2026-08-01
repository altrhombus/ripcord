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
