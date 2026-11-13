using System.Linq;
using System.Text;
using System.Text.Json;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Pins the shape of our v2 launchSpec against what the console's parser is observed to accept: the field set,
/// the standard base64 encoding of the handshakeKey, the OFB round-trip under <see cref="HalyardV2SessionCrypto"/>,
/// and determinism across calls. Each assertion covers a separable link in the handshakeKey -> wire -> console
/// pipeline, so a regression in any one of them lands on a specific test rather than a vague "the console stopped
/// signing correctly" symptom at integration time.
/// </summary>
public class V2LaunchSpecVendorDiffTests
{
    // An arbitrary 16-byte test key. The handshakeKey is random per session on the wire; this pattern is only
    // used to drive the assertions below.
    private static readonly byte[] TestHandshakeKey = Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();

    [Fact]
    public void LaunchSpec_CarriesEveryFieldTheConsoleParserExpects()
    {
        // The console's parser rejects an incomplete document silently (no SESSION_REPLY), so missing any one
        // of these is a concrete way to produce a stream-handshake failure with no diagnostic surface.
        string json = HalyardStreamingSession.BuildLaunchSpecJson(
            TestHandshakeKey,
            w: 1920, h: 1080, fps: 60,
            bitrate: 20_000,
            codec: VideoCodec.H264);

        using var doc = JsonDocument.Parse(json);

        string[] required = {
            "sessionId",
            "streamResolutions",
            "network",
            "slotId",
            "appSpecification",
            "konan",
            "requestGameSpecification",
            "userProfile",
            "videoCodec",
            "dynamicRange",
            "handshakeKey",
            "audioChannels",
        };
        foreach (var field in required)
        {
            Assert.True(doc.RootElement.TryGetProperty(field, out _), $"launchSpec missing required field \"{field}\"");
        }
    }

    [Fact]
    public void LaunchSpec_HandshakeKey_IsStandardBase64_RoundTripsToExactBytes()
    {
        // The console's standard-base64 decoder recovers the exact 16 bytes only when we emit standard base64
        // (padding '==', no url-safe '-/_', no whitespace). If the encoder drifted (e.g., to url-safe) the
        // console would decode different bytes and sign its stream pubkey with those instead.
        string json = HalyardStreamingSession.BuildLaunchSpecJson(
            TestHandshakeKey, 1920, 1080, 60, 20_000, VideoCodec.H264);

        using var doc = JsonDocument.Parse(json);
        string hk = doc.RootElement.GetProperty("handshakeKey").GetString()!;

        Assert.Equal(Convert.ToBase64String(TestHandshakeKey), hk);
        Assert.Equal(TestHandshakeKey, Convert.FromBase64String(hk));

        // Standard base64 of 16 bytes: 24 chars, '==' tail, no url-safe variants, no whitespace.
        Assert.Equal(24, hk.Length);
        Assert.EndsWith("==", hk);
        Assert.DoesNotContain('-', hk);
        Assert.DoesNotContain('_', hk);
        Assert.DoesNotContain(' ', hk);
        Assert.DoesNotContain('\n', hk);
        Assert.DoesNotContain('\r', hk);
    }

    [Fact]
    public void LaunchSpec_OfbRoundTrip_PreservesHandshakeKey()
    {
        // The full client-side wire path is BuildLaunchSpecJson -> UTF-8 ->
        // HalyardV2SessionCrypto.CryptStreaminfo(counter=0) -> base64 -> on the wire. The console reverses it.
        // If any step corrupts the handshakeKey bytes (counter drift, keying off the wrong material, OFB
        // boundary off-by-one), the console recovers something ELSE for handshakeKey and signs with that.
        var secrets = TestSecrets.SyntheticControlSecrets();
        var material = new HalyardControlKeyMaterial(
            Nonce: Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
            Companion: Enumerable.Range(0x40, 16).Select(i => (byte)i).ToArray(),
            CodecSelector: 0,
            VersionSelector: 1); // PS5

        using var v2 = new HalyardV2SessionCrypto(secrets);
        v2.EstablishControl(material);

        byte[] plain = Encoding.UTF8.GetBytes(HalyardStreamingSession.BuildLaunchSpecJson(
            TestHandshakeKey, 1920, 1080, 60, 20_000, VideoCodec.H264));

        byte[] ciphertext = v2.CryptStreaminfo(0, plain);
        // The ciphertext MUST differ from the plaintext — this catches a silent fall-through where the cipher
        // wasn't established and CryptStreaminfo returned the plaintext unchanged.
        Assert.NotEqual(Convert.ToHexString(plain), Convert.ToHexString(ciphertext));

        // OFB at the same counter is symmetric: the console recovers the bytes we sent.
        byte[] recovered = v2.CryptStreaminfo(0, ciphertext);
        Assert.Equal(plain, recovered);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(recovered));
        Assert.Equal(Convert.ToBase64String(TestHandshakeKey),
            doc.RootElement.GetProperty("handshakeKey").GetString());
        Assert.Equal(TestHandshakeKey,
            Convert.FromBase64String(doc.RootElement.GetProperty("handshakeKey").GetString()!));
    }

    [Fact]
    public void LaunchSpec_SameHandshakeKey_ProducesByteIdenticalDocument_TwoCalls()
    {
        // Determinism: given the same handshakeKey and the same configuration, the launchSpec bytes are
        // identical across calls. Catches any inadvertent randomness or timestamp inclusion that would make
        // the handshakeKey the client holds (fetched once) diverge from the one the console decrypts from the
        // wire bytes.
        string a = HalyardStreamingSession.BuildLaunchSpecJson(TestHandshakeKey, 1920, 1080, 60, 20_000, VideoCodec.H264);
        string b = HalyardStreamingSession.BuildLaunchSpecJson(TestHandshakeKey, 1920, 1080, 60, 20_000, VideoCodec.H264);
        Assert.Equal(a, b);
        Assert.Equal(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
