using System.Security.Cryptography;
using System.Text;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The account ("web"/no-PIN) registration seed delivery — the mechanism recovered 2026-08-31: the console
/// delivers the 16-byte registration seed <em>encrypted</em> over the PSN cloud, field-encrypted with the
/// client's ephemeral <c>data1</c>/<c>data2</c> and shipped as the double-base64 <c>customData1</c> push
/// value. These pin the wire encoding, the seal/recover round-trip, and the full account request path
/// (<c>key' = seed XOR registrationTable[selector]</c>) against the bundled interop constants.
/// </summary>
public sealed class HalyardAccountSeedDeliveryTests
{
    // ---- customData1 wire encoding (double-base64) ----

    [Fact]
    public void CustomData1_IsDoubleBase64_RoundTrips()
    {
        byte[] ciphertext = RandomNumberGenerator.GetBytes(17);
        string wire = HalyardAccountSeedDelivery.EncodeCustomData1(ciphertext);

        // Outer layer is base64 of an *ASCII base64 string* (not of the raw bytes).
        string inner = Encoding.ASCII.GetString(Convert.FromBase64String(wire));
        Assert.Equal(Convert.ToBase64String(ciphertext), inner);

        Assert.Equal(ciphertext, HalyardAccountSeedDelivery.DecodeCustomData1(wire));
    }

    // ---- seal (console side) -> recover (client side) round-trip ----

    [SkippableFact]
    public void SealThenRecover_ReturnsTheSeed()
    {
        byte[] contextKey = BundledContextKeyOrSkip();
        var (data1, data2) = HalyardAccountSeedDelivery.GenerateEphemeralKeyMaterial();
        byte[] seed = RandomNumberGenerator.GetBytes(16);

        // Console: field-encrypt the seed with the client's data1/data2, ship as customData1.
        byte[] ciphertext = HalyardAccountSeedDelivery.SealSeed(data1, data2, seed, contextKey);
        string customData1 = HalyardAccountSeedDelivery.EncodeCustomData1(ciphertext);

        // Client: decode customData1 and field-decrypt with the same data1/data2.
        byte[] recovered = HalyardAccountSeedDelivery.RecoverSeed(data1, data2, customData1, contextKey);

        Assert.Equal(seed, recovered);
    }

    [Fact]
    public void GenerateEphemeralKeyMaterial_ProducesDistinct16ByteValues()
    {
        var (a1, a2) = HalyardAccountSeedDelivery.GenerateEphemeralKeyMaterial();
        var (b1, _) = HalyardAccountSeedDelivery.GenerateEphemeralKeyMaterial();
        Assert.Equal(16, a1.Length);
        Assert.Equal(16, a2.Length);
        Assert.NotEqual(a1, a2);   // key != material
        Assert.NotEqual(a1, b1);   // fresh per call
    }

    [SkippableFact]
    public void RecoverSeed_RejectsWrongData1()
    {
        byte[] contextKey = BundledContextKeyOrSkip();
        var (data1, data2) = HalyardAccountSeedDelivery.GenerateEphemeralKeyMaterial();
        byte[] seed = RandomNumberGenerator.GetBytes(16);
        byte[] ciphertext = HalyardAccountSeedDelivery.SealSeed(data1, data2, seed, contextKey);

        byte[] wrongData1 = (byte[])data1.Clone();
        wrongData1[0] ^= 0xFF;
        byte[] recovered = HalyardAccountSeedDelivery.RecoverSeed(wrongData1, data2, ciphertext, contextKey);

        Assert.NotEqual(seed, recovered);   // a wrong key gives garbage, not the seed
    }

    // ---- full account request: key' = seed XOR registrationTable[selector] ----

    [SkippableFact]
    public void AccountRequest_FieldRoundTrips_UnderSeedXorTableKey()
    {
        var reg = RegistrationOrSkip();

        byte[] seed = RandomNumberGenerator.GetBytes(16);
        byte[] plaintext = Encoding.ASCII.GetBytes("Client-Type: Windows\r\nNp-AccountId: AAAAAAAAAAA=\r\n");

        var exchange = reg.Cipher.BuildAccountRequest(seed, plaintext);

        // The request body is [context][encrypted field]; the console recovers the material from the context
        // and decrypts the field with key' = seed XOR registrationTable[selector].
        byte[] field = exchange.RequestBody.AsSpan(HalyardRegistrationCipher.ContextLength).ToArray();
        byte[] material = reg.Cipher.RecoverMaterial(exchange.RequestBody.AsSpan(0, HalyardRegistrationCipher.ContextLength));
        byte[] decrypted = reg.Cipher.DecryptAccountField(
            exchange.RequestBody.AsSpan(0, HalyardRegistrationCipher.ContextLength), seed, material, field);

        Assert.Equal(plaintext, decrypted);
    }

    [SkippableFact]
    public void AccountRequest_DecryptResponse_UsesTheSeedKey()
    {
        var reg = RegistrationOrSkip();

        byte[] seed = RandomNumberGenerator.GetBytes(16);
        var exchange = reg.Cipher.BuildAccountRequest(seed, Encoding.ASCII.GetBytes("x"));
        byte[] context = exchange.RequestBody.AsSpan(0, HalyardRegistrationCipher.ContextLength).ToArray();

        // Console seals a response field with the same key' + material; the client decrypts it via the exchange.
        byte[] response = Encoding.ASCII.GetBytes("PS5-RegistKey: 3161326233633464\r\n");
        byte[] sealedResponse = SealAccountResponse(reg, context, seed, exchange, response);

        Assert.Equal(response, reg.Cipher.DecryptResponse(exchange, sealedResponse));
    }

    // ---- helpers ----

    private static byte[] SealAccountResponse(
        (HalyardRegistrationCipher Cipher, byte[] ContextKey, HalyardRegistrationKdf Kdf) reg,
        byte[] context, byte[] seed, HalyardRegistrationExchange exchange, byte[] plaintext)
    {
        // key' = seed XOR registrationTable[selector]; material recovered from the context (same as console).
        byte[] key = reg.Kdf.DeriveKey(context, 0);
        for (int i = 0; i < key.Length; i++) key[i] ^= seed[i];
        byte[] material = reg.Cipher.RecoverMaterial(context);
        return new HalyardControlFieldCrypto(key, material, reg.ContextKey)
            .EncryptField(HalyardRegistrationCipher.FieldCounter, plaintext);
    }

    private static byte[] BundledContextKeyOrSkip()
    {
        var bundled = HalyardInteropConstants.Registration(HalyardConsolePlatform.Ps5);
        Skip.If(bundled is null, "Build omitted the interop constants (-p:BundleInteropConstants=false).");
        return bundled!.Value.ContextKey;
    }

    private static (HalyardRegistrationCipher Cipher, byte[] ContextKey, HalyardRegistrationKdf Kdf) RegistrationOrSkip()
    {
        var bundled = HalyardInteropConstants.Registration(HalyardConsolePlatform.Ps5);
        Skip.If(bundled is null, "Build omitted the interop constants (-p:BundleInteropConstants=false).");
        var b = bundled!.Value;
        var kdf = new HalyardRegistrationKdf(b.Secrets, b.VersionSelector);
        return (new HalyardRegistrationCipher(kdf, b.ContextKey), b.ContextKey, kdf);
    }
}
