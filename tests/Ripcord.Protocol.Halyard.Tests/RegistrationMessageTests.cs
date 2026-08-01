using System.Text;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Structural registration tests (spec §2.0) - endpoint selection, HTTP request framing, response splitting,
/// and pairing-record parsing. This half carries no secret, so it is fully exercised here with synthetic
/// fixtures. The body cipher (<see cref="IHalyardRegistrationCipher"/>) is validated separately in
/// <see cref="LiveRegistrationVectorTests"/>.
/// </summary>
public class RegistrationMessageTests
{
    [Theory]
    [InlineData(HalyardConsolePlatform.Ps5, "/sie/ps5/rp/sess/rgst")]
    [InlineData(HalyardConsolePlatform.Ps4, "/sie/ps4/rp/sess/rgst")]
    public void EndpointPath_SelectsByPlatform(HalyardConsolePlatform platform, string expected)
        => Assert.Equal(expected, HalyardRegistrationMessage.EndpointPath(platform));

    [Fact]
    public void BuildRequest_FramesPostWithWireHeaders()
    {
        var request = new HalyardRegistrationRequest(
            ConsoleId: "console-1",
            ConsoleHost: "192.0.2.10",
            AccountId: "1234567890123456789",
            Passcode: "12345678",
            ClientDeviceId: new byte[16],
            Platform: HalyardConsolePlatform.Ps5);

        byte[] body = Encoding.ASCII.GetBytes("ENCRYPTED-BODY");
        byte[] wire = HalyardRegistrationMessage.BuildRequest(request, "192.0.2.55", body);
        string text = Encoding.ASCII.GetString(wire);

        Assert.StartsWith("POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n", text);
        Assert.Contains("HOST: 192.0.2.55\r\n", text);                 // uppercase, the CLIENT's IP, no port
        Assert.Contains("User-Agent: remoteplay Windows\r\n", text);
        Assert.Contains("RP-Version: 1.0\r\n", text);
        Assert.Contains("Content-Length: 14\r\n", text);
        Assert.DoesNotContain("Np-AccountId:", text);                  // account id lives inside the body
        Assert.EndsWith("\r\n\r\nENCRYPTED-BODY", text);
    }

    [Fact]
    public void BuildRequestFieldPlaintext_HasClientTypeAndAccountId()
    {
        // AccountId is a synthetic 19-digit value. Never paste a real PSN account id here: nothing in this test
        // depends on it (only that an Np-AccountId line is present), and a real one would be published verbatim.
        var request = new HalyardRegistrationRequest(
            ConsoleId: "c", ConsoleHost: "192.0.2.10", AccountId: "1234567890123456789",
            Passcode: "12345678", ClientDeviceId: Convert.FromHexString(new string('a', 32)),
            Platform: HalyardConsolePlatform.Ps5);

        string text = Encoding.ASCII.GetString(HalyardRegistrationMessage.BuildRequestFieldPlaintext(request));
        // Client-Type is the fixed client identifier, not the per-device id.
        Assert.StartsWith("Client-Type: " + HalyardRegistrationMessage.ClientTypeHex + "\r\n", text);
        Assert.Contains("Np-AccountId: ", text);
        Assert.EndsWith("\r\n", text);
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\nBODY", 200, "BODY")]
    [InlineData("HTTP/1.1 403 Forbidden\r\n\r\n", 403, "")] // non-2xx returns false but reports the code
    public void TrySplitResponse_ReadsStatusAndBody(string raw, int expectedStatus, string expectedBody)
    {
        bool ok = HalyardRegistrationMessage.TrySplitResponse(Encoding.ASCII.GetBytes(raw), out int status, out byte[] body);

        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedStatus is >= 200 and < 300, ok);
        Assert.Equal(expectedBody, Encoding.ASCII.GetString(body));
    }

    [Fact]
    public void TryParsePairingRecord_ExtractsRegistKeyCompanionAndKeyType()
    {
        // Synthetic decrypted response body: field names are [C], values are made-up. PS5-RegistKey is the
        // hex of the raw registration-key bytes, so it must be decoded (not stored as the hex string).
        string companionHex = "000102030405060708090a0b0c0d0e0f"; // 16 bytes
        string body = string.Join("\r\n",
            "PS5-RegistKey: 3161326233633464", // hex of the 8 raw bytes "1a2b3c4d"
            "RP-Key: " + companionHex,
            "RP-KeyType: 3",
            "PS5-Nickname: LivingRoom",
            "PS5-Mac: 001122334455");

        bool ok = HalyardRegistrationMessage.TryParsePairingRecord(Encoding.ASCII.GetBytes(body), out HalyardPairingRecord? record);

        Assert.True(ok);
        Assert.NotNull(record);
        // Stored as the raw 8 bytes (hex-decoded), which /sess/init re-hex-encodes and RP-Auth zero-pads.
        Assert.Equal("3161326233633464", Convert.ToHexString(record!.RegistrationKey).ToLowerInvariant());
        Assert.Equal("1a2b3c4d", Encoding.ASCII.GetString(record.RegistrationKey));
        Assert.Equal(companionHex, Convert.ToHexString(record.Companion).ToLowerInvariant());
        Assert.Equal(3, record.KeyType);
    }

    [Fact]
    public void TryParsePairingRecord_FailsWhenRequiredFieldsMissing()
    {
        bool ok = HalyardRegistrationMessage.TryParsePairingRecord(
            Encoding.ASCII.GetBytes("PS5-Nickname: LivingRoom\r\n"), out HalyardPairingRecord? record);

        Assert.False(ok);
        Assert.Null(record);
    }

    [Fact]
    public void ParsedPairingRecord_RoundTripsThroughStore()
    {
        string body = "PS5-RegistKey: KEY01234\r\nRP-Key: 0f0e0d0c0b0a09080706050403020100\r\nRP-KeyType: 2\r\n";
        Assert.True(HalyardRegistrationMessage.TryParsePairingRecord(Encoding.ASCII.GetBytes(body), out HalyardPairingRecord? record));

        byte[] blob = record!.Serialize();
        Assert.True(HalyardPairingRecord.TryDeserialize(blob, out HalyardPairingRecord? restored));
        Assert.NotNull(restored);
        Assert.Equal(Convert.ToHexString(record.RegistrationKey), Convert.ToHexString(restored!.RegistrationKey));
        Assert.Equal(Convert.ToHexString(record.Companion), Convert.ToHexString(restored.Companion));
        Assert.Equal(record.KeyType, restored.KeyType);
    }
}
