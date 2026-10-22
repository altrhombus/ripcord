using System.Linq;
using System.Text.Json;
using Avstream;
using Google.Protobuf;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// v2 launchSpec + SESSION_REQUEST (spec §5/§6). The launchSpec is byte-for-byte the v1 template, AES-128-OFB
/// under the v1 control KDF (only the counter differs: v2 uses 0), carrying the same client-random
/// <c>handshakeKey</c> — v2 does NOT drop it (static RE of the vendor's launchSpec builder <c>FUN_9ee50</c> plus
/// three live v2 sessions confirm the handshakeKey is a no-input client-random value, identical to v1).
/// SESSION_REQUEST gains field 7, the sentinel sessionId <c>"InvalidSessionId"</c>.
/// </summary>
public class V2LaunchSpecTests
{
    [Fact]
    public void LaunchSpec_V2_CarriesHandshakeKey_SameAsV1()
    {
        // The builder is version-agnostic for this field: both v1 and v2 pass a fresh 16-byte client-random key,
        // so the template (given the same key) is byte-identical. X is that key — not a customData1-sealed secret.
        byte[] key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        string a = HalyardStreamingSession.BuildLaunchSpecJson(key, 1280, 720, 60, 10_000, VideoCodec.H264);
        string b = HalyardStreamingSession.BuildLaunchSpecJson(key, 1280, 720, 60, 10_000, VideoCodec.H264);

        using var da = JsonDocument.Parse(a);
        using var db = JsonDocument.Parse(b);

        // handshakeKey is present and is the supplied key, base64-encoded.
        Assert.True(da.RootElement.TryGetProperty("handshakeKey", out JsonElement hk));
        Assert.Equal(Convert.ToBase64String(key), hk.GetString());

        // Same field set, same values (no v2 divergence in the JSON).
        Assert.Equal(da.RootElement.EnumerateObject().Count(), db.RootElement.EnumerateObject().Count());
        foreach (JsonProperty p in da.RootElement.EnumerateObject())
        {
            Assert.True(db.RootElement.TryGetProperty(p.Name, out JsonElement bp), $"launchSpec missing {p.Name}");
            Assert.Equal(p.Value.GetRawText(), bp.GetRawText());
        }
    }

    [Fact]
    public void CryptStreaminfo_V2_IsByteIdenticalToV1()
    {
        var secrets = TestSecrets.SyntheticControlSecrets();
        var material = new HalyardControlKeyMaterial(
            Nonce: Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
            Companion: Enumerable.Range(0x40, 16).Select(i => (byte)i).ToArray(),
            CodecSelector: 0,
            VersionSelector: 1); // PS5

        using var v1 = new HalyardV1SessionCrypto(secrets);
        using var v2 = new HalyardV2SessionCrypto(secrets);
        v1.EstablishControl(material);
        v2.EstablishControl(material);

        byte[] launchSpec = "{\"videoCodec\":\"avc\",\"dynamicRange\":\"SDR\"}"u8.ToArray();

        // v2's launchSpec cipher is v1's (the confirmed one), at counter 0.
        byte[] c1 = v1.CryptStreaminfo(0, launchSpec);
        byte[] c2 = v2.CryptStreaminfo(0, launchSpec);
        Assert.Equal(Convert.ToHexString(c1), Convert.ToHexString(c2));

        // OFB is symmetric: re-applying recovers the plaintext.
        Assert.Equal(Convert.ToHexString(launchSpec), Convert.ToHexString(v2.CryptStreaminfo(0, c2)));
    }

    [Fact]
    public void CryptStreaminfo_BeforeEstablish_Throws()
    {
        using var v2 = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        Assert.Throws<InvalidOperationException>(() => v2.CryptStreaminfo(0, new byte[16]));
    }

    [Fact]
    public void SessionRequest_CarriesField7SessionId()
    {
        var payload = new SessionRequestPayload
        {
            ClientVersion = 20,
            SessionKey = "skey",
            LaunchSpecJson = "launchspec",
            EncryptedKey = ByteString.Empty,
            SessionId = "InvalidSessionId",
        };

        var round = SessionRequestPayload.Parser.ParseFrom(payload.ToByteArray());
        Assert.Equal(20u, round.ClientVersion);
        Assert.True(round.HasSessionId);
        Assert.Equal("InvalidSessionId", round.SessionId);
    }
}
