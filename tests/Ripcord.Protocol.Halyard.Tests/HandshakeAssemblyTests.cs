using System.Security.Cryptography;
using System.Text;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Tests for the v1 handshake-assembly pieces: the persistent pairing record (serialize/deserialize) and
/// the encrypted <c>/sess/ctrl</c> field construction (spec §2.1). Field plaintext shapes are checked
/// against the confirmed [V] forms; a builder-through-seam round-trip checks the wiring.
/// </summary>
public class HandshakeAssemblyTests
{
    [Fact]
    public void PairingRecord_RoundTrips()
    {
        var original = new HalyardPairingRecord(
            RegistrationKey: Encoding.ASCII.GetBytes("abcd1234"),
            Companion: Hex.Bytes("2a3b4c5d6e7f8091a2b3c4d5e6f70819"),
            KeyType: 3);

        Assert.True(HalyardPairingRecord.TryDeserialize(original.Serialize(), out var restored));
        Assert.NotNull(restored);
        Assert.Equal(Hex.String(original.RegistrationKey), Hex.String(restored!.RegistrationKey));
        Assert.Equal(Hex.String(original.Companion), Hex.String(restored.Companion));
        Assert.Equal(original.KeyType, restored.KeyType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]        // wrong format version
    [InlineData("01ffff")]    // truncated length-prefixed field
    public void PairingRecord_RejectsMalformedBlob(string hex)
    {
        Assert.False(HalyardPairingRecord.TryDeserialize(Hex.Bytes(hex), out var record));
        Assert.Null(record);
    }

    [Fact]
    public void AuthPlaintext_IsRegistrationKeyZeroPaddedTo16()
    {
        var plaintext = HalyardSessCtrlFields.BuildAuthPlaintext(Encoding.ASCII.GetBytes("abcd1234"));
        Assert.Equal("61626364313233340000000000000000", Hex.String(plaintext));
    }

    [Fact]
    public void OsTypePlaintext_IsNulTerminatedWinString()
    {
        // "Win10.0\0"
        Assert.Equal("57696e31302e3000", Hex.String(HalyardSessCtrlFields.BuildOsTypePlaintext(10, 0)));
    }

    [Fact]
    public void DidPlaintext_IsThe32ByteDeviceIdStructure()
    {
        var deviceId = Hex.Bytes("00112233445566778899aabbccddeeff"); // 16-byte hex-decoded MachineGuid
        var plaintext = HalyardSessCtrlFields.BuildDidPlaintext(deviceId);
        // 10-byte marker prefix + the 16 device bytes + 6 zero bytes = 32 bytes (matches cap22).
        Assert.Equal(32, plaintext.Length);
        Assert.Equal(
            "00180000000700400080" + "00112233445566778899aabbccddeeff" + "000000000000",
            Hex.String(plaintext));
    }

    [Fact]
    public void Int32Plaintext_IsLittleEndian()
        => Assert.Equal("01000000", Hex.String(HalyardSessCtrlFields.BuildInt32Plaintext(1)));

    [Fact]
    public void Build_ProducesFiveFieldsInCounterOrder_AndRoundTripsThroughSeam()
    {
        using var crypto = new HalyardV1SessionCrypto(TestSecrets.SyntheticControlSecrets());
        crypto.EstablishControl(new HalyardControlKeyMaterial(
            Nonce: Hex.Bytes("1a2b3c4d5e6f708192a3b4c5d6e7f809"),
            Companion: Hex.Bytes("2a3b4c5d6e7f8091a2b3c4d5e6f70819"),
            CodecSelector: 2, VersionSelector: 1));

        var registrationKey = Encoding.ASCII.GetBytes("abcd1234");
        var deviceId = Hex.Bytes("00112233445566778899aabbccddeeff");
        var fields = HalyardSessCtrlFields.Build(crypto, registrationKey, deviceId, 10, 0, startBitrate: 10_000, streamingType: 0, platform: HalyardConsolePlatform.Ps5);

        Assert.Equal(
            new[] { SessProtocol.HeaderAuth, SessProtocol.HeaderDid, SessProtocol.HeaderOsType, SessProtocol.HeaderStartBitrate, SessProtocol.HeaderStreamingType },
            fields.Select(f => f.Key).ToArray());

        // PS4 carries four headers, not five - RP-StreamingType is not one of them - which is what puts the
        // login passcode at counter 4 instead of 5. A capture fact; see HalyardSessCtrlFields.LoginPinCounter.
        var ps4 = HalyardSessCtrlFields.Build(crypto, registrationKey, deviceId, 10, 0, startBitrate: 10_000, streamingType: 0, platform: HalyardConsolePlatform.Ps4);
        Assert.Equal(
            new[] { SessProtocol.HeaderAuth, SessProtocol.HeaderDid, SessProtocol.HeaderOsType, SessProtocol.HeaderStartBitrate },
            ps4.Select(f => f.Key).ToArray());
        Assert.Equal(5ul, HalyardSessCtrlFields.LoginPinCounter(HalyardConsolePlatform.Ps5));
        Assert.Equal(4ul, HalyardSessCtrlFields.LoginPinCounter(HalyardConsolePlatform.Ps4));

        // Every value is valid base64; RP-Auth decrypts back to the zero-padded registration key.
        foreach (var (_, value) in fields)
        {
            Assert.NotEmpty(value);
            _ = Convert.FromBase64String(value);
        }

        var authCipher = Convert.FromBase64String(fields[0].Value);
        var recovered = crypto.DecryptControlField(HalyardSessCtrlFields.CounterAuth, authCipher);
        Assert.Equal(Hex.String(HalyardSessCtrlFields.BuildAuthPlaintext(registrationKey)), Hex.String(recovered));
    }
}

internal static class TestSecrets
{
    /// <summary>
    /// A synthetic <see cref="HalyardControlSecrets"/> (random tables, zero context keys). The control
    /// crypto only needs the KDF to be deterministic for round-trip tests; real-console values live in the
    /// gitignored dirty-room fixture.
    /// </summary>
    public static HalyardControlSecrets SyntheticControlSecrets()
    {
        var t1 = new byte[HalyardControlSecrets.KdfTableLength];
        var t2 = new byte[HalyardControlSecrets.KdfTableLength];
        RandomNumberGenerator.Fill(t1);
        RandomNumberGenerator.Fill(t2);
        var keys = new HalyardFieldContextKeys(new byte[16], new byte[16], new byte[16], new byte[16]);
        return new HalyardControlSecrets(t1, t2, keys);
    }
}
