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
/// v2 launchSpec + SESSION_REQUEST (spec §5/§6). The launchSpec is the v1 template minus <c>handshakeKey</c>,
/// AES-128-OFB under the v1 control KDF at counter 0 — so the cipher is v1's (already KAT'd against captures) and
/// v2 reuses it; only the counter and the dropped key differ. SESSION_REQUEST gains field 7, the sentinel
/// sessionId <c>"InvalidSessionId"</c>.
/// </summary>
public class V2LaunchSpecTests
{
    [Fact]
    public void LaunchSpec_V2_DropsHandshakeKey_OtherwiseMatchesV1()
    {
        byte[] key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        string v1 = HalyardStreamingSession.BuildLaunchSpecJson(key, 1280, 720, 60, 10_000, VideoCodec.H264);
        string v2 = HalyardStreamingSession.BuildLaunchSpecJson(null, 1280, 720, 60, 10_000, VideoCodec.H264);

        using var d1 = JsonDocument.Parse(v1);
        using var d2 = JsonDocument.Parse(v2);

        Assert.True(d1.RootElement.TryGetProperty("handshakeKey", out _));
        Assert.False(d2.RootElement.TryGetProperty("handshakeKey", out _));

        // Every other top-level field is byte-identical, and v2 has exactly one fewer.
        foreach (JsonProperty p in d1.RootElement.EnumerateObject())
        {
            if (p.Name == "handshakeKey")
                continue;
            Assert.True(d2.RootElement.TryGetProperty(p.Name, out JsonElement v2p), $"v2 launchSpec missing {p.Name}");
            Assert.Equal(p.Value.GetRawText(), v2p.GetRawText());
        }

        Assert.Equal(d1.RootElement.EnumerateObject().Count() - 1, d2.RootElement.EnumerateObject().Count());
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
