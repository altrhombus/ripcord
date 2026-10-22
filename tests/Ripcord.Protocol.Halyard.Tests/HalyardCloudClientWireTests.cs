using System.Net;
using System.Text;
using Ripcord.Cloud.Halyard;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The cloud client's request bodies, byte for byte, and its reading of each response.
///
/// <para>These bodies go to PSN and on to the console, and several details in them were matched against our own
/// captures on purpose (a numeric accountId, empty-but-present fields, field order). The strings below were
/// taken from the client as it stood on 2026-09-27, before its JSON moved to source generation, so that the
/// move could be shown to change nothing on the wire.</para>
/// </summary>
public class HalyardCloudClientWireTests
{
    private sealed class Wire : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            Sent.Add((request.Method.Method, path, body));
            string reply = path switch
            {
                _ when path.EndsWith("/sessionMessage", StringComparison.Ordinal) => "",
                _ when path.Contains("remotePlaySessions", StringComparison.Ordinal) =>
                    """{"remotePlaySessions":[{"sessionId":"s-1","members":[{"accountId":"42","platform":"PS5","deviceUniqueId":"d-1"}]}]}""",
                _ when path.Contains("commands", StringComparison.Ordinal) => """{"commandId":"c-1"}""",
                _ when path.Contains("serveraddr", StringComparison.OrdinalIgnoreCase) =>
                    """{"fqdn":"push.example","keepAliveStatus":{"clientKeepAliveInterval":1,"clientKeepAliveTimeout":2,"serverKeepAliveTimeout":3,"serverPresenceTimeout":4}}""",
                _ when path.Contains("clients", StringComparison.Ordinal) =>
                    """{"clients":[{"device":{"name":"PS5-8A2F","wakeupEnabledPowerModes":["STANDBY"],"enabledFeatures":["remotePlay"]},"duid":"d-1","platform":"PS5"}]}""",
                _ => """{"accountId":"42","onlineId":"someone","region":"gb"}""",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private static (HalyardCloudClient Client, Wire Wire) Build()
    {
        var wire = new Wire();
        var auth = new HalyardAuthClient(new HttpClient(wire), new HalyardClientConfig("id", "secret", HalyardClientConfig.DefaultRedirectUri, HalyardClientConfig.DefaultScopes));
        var tokens = new HalyardTokenProvider(auth);
        tokens.Seed(new HalyardTokens("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        return (new HalyardCloudClient(new HttpClient(wire), tokens), wire);
    }

    private const string CreateSession =
        """
        {"remotePlaySessions":[{"members":[{"accountId":"me","deviceUniqueId":"me","platform":"me","pushContexts":[{"pushContextId":"push-ctx"}]}]}]}
        """;

    private const string ConnectNumeric =
        """
        {"commandDetail":{"platform":"PS5","duid":"d-1","commandType":"remotePlay","parameters":{"initialParams":"{\u0022accountId\u0022:1234567890123456789, \u0022roomId\u0022:0, \u0022sessionId\u0022:\u0022s-1\u0022, \u0022clientType\u0022:\u0022ct\\u0022q\u0022, \u0022data1\u0022:\u0022a\u0022, \u0022data2\u0022:\u0022b\u0022}"},"messageDestination":"SQS"}}
        """;

    private const string ConnectNotNumeric =
        """
        {"commandDetail":{"platform":"PS5","duid":"d-1","commandType":"remotePlay","parameters":{"initialParams":"{\u0022accountId\u0022:\u0022not-numeric\u0022, \u0022roomId\u0022:0, \u0022sessionId\u0022:\u0022s-1\u0022, \u0022clientType\u0022:\u0022ct\u0022, \u0022data1\u0022:\u0022a\u0022, \u0022data2\u0022:\u0022b\u0022}"},"messageDestination":"SQS"}}
        """;

    private const string OfferWithCandidates =
        """
        {"channel":"remote_play:1","payload":"ver=1.0, type=text, body={\u0022action\u0022:\u0022OFFER\u0022,\u0022reqId\u0022:3,\u0022error\u0022:0,\u0022connRequest\u0022:{\u0022sid\u0022:2,\u0022peerSid\u0022:0,\u0022skey\u0022:\u0022AAAAAAAAAAAAAAAAAAAAAA==\u0022,\u0022natType\u0022:2,\u0022candidate\u0022:[{\u0022type\u0022:\u0022LOCAL\u0022,\u0022addr\u0022:\u0022192.168.1.5\u0022,\u0022mappedAddr\u0022:\u00220.0.0.0\u0022,\u0022port\u0022:50000,\u0022mappedPort\u0022:0},{\u0022type\u0022:\u0022STUN\u0022,\u0022addr\u0022:\u0022203.0.113.9\u0022,\u0022mappedAddr\u0022:\u00220.0.0.0\u0022,\u0022port\u0022:61000,\u0022mappedPort\u0022:0}],\u0022defaultRouteMacAddr\u0022:\u0022\u0022,\u0022localPeerAddr\u0022:{\u0022accountId\u0022:\u002242\u0022,\u0022platform\u0022:\u0022REMOTE_PLAY\u0022},\u0022localHashedId\u0022:\u0022AQID\u0022}}","to":[{"accountId":"42","deviceUniqueId":"d-1","platform":"PS5"}]}
        """;

    private const string OfferEmpty =
        """
        {"channel":"remote_play:1","payload":"ver=1.0, type=text, body={\u0022action\u0022:\u0022OFFER\u0022,\u0022reqId\u0022:1,\u0022error\u0022:0,\u0022connRequest\u0022:{\u0022sid\u0022:1,\u0022peerSid\u0022:0,\u0022skey\u0022:\u0022AAAAAAAAAAAAAAAAAAAAAA==\u0022,\u0022natType\u0022:2,\u0022candidate\u0022:[],\u0022defaultRouteMacAddr\u0022:\u0022\u0022,\u0022localPeerAddr\u0022:{\u0022accountId\u0022:\u002242\u0022,\u0022platform\u0022:\u0022REMOTE_PLAY\u0022},\u0022localHashedId\u0022:\u0022\u0022}}","to":[{"accountId":"42","deviceUniqueId":"d-1","platform":"PS5"}]}
        """;

    private const string Result =
        """
        {"channel":"remote_play:1","payload":"ver=1.0, type=text, body={\u0022action\u0022:\u0022RESULT\u0022,\u0022reqId\u0022:7,\u0022error\u0022:0,\u0022connRequest\u0022:{}}","to":[{"accountId":"42","deviceUniqueId":"d-1","platform":"PS5"}]}
        """;

    private const string Accept =
        """
        {"channel":"remote_play:1","payload":"ver=1.0, type=text, body={\u0022action\u0022:\u0022ACCEPT\u0022,\u0022reqId\u0022:2,\u0022error\u0022:0,\u0022connRequest\u0022:{\u0022sid\u0022:1,\u0022peerSid\u0022:5,\u0022skey\u0022:\u0022AAAAAAAAAAAAAAAAAAAAAA==\u0022,\u0022natType\u0022:0,\u0022candidate\u0022:[{\u0022type\u0022:\u0022LOCAL\u0022,\u0022addr\u0022:\u0022192.168.1.20\u0022,\u0022mappedAddr\u0022:\u0022192.168.1.5\u0022,\u0022port\u0022:9303,\u0022mappedPort\u0022:50000}],\u0022defaultRouteMacAddr\u0022:\u0022\u0022,\u0022localPeerAddr\u0022:,\u0022localHashedId\u0022:\u0022\u0022}}","to":[{"accountId":"42","deviceUniqueId":"d-1","platform":"PS5"}]}
        """;

    [Fact]
    public async Task RequestBodies_AreByteForByteWhatTheyWere()
    {
        var (client, wire) = Build();
        await client.CreateSessionAsync("push-ctx", CancellationToken.None);
        await client.SendConnectCommandAsync("d-1", "1234567890123456789", "s-1", "ct\"q", ("a", "b", "c"), CancellationToken.None);
        await client.SendConnectCommandAsync("d-1", "not-numeric", "s-1", "ct", ("a", "b", "c"), CancellationToken.None);
        await client.SendOfferAsync("s-1", "42", "d-1",
            [new HalyardCandidate("LOCAL", "192.168.1.5", 50000), new HalyardCandidate("STUN", "203.0.113.9", 61000)],
            CancellationToken.None, localHashedId: new byte[] { 1, 2, 3 }, reqId: 3, sid: 2);
        await client.SendOfferAsync("s-1", "42", "d-1", [], CancellationToken.None);
        await client.SendResultAsync("s-1", "42", "d-1", 7, CancellationToken.None);
        await client.SendAcceptAsync("s-1", "42", "d-1", 2, 1, 5, new HalyardCandidate("LOCAL", "192.168.1.20", 9303),
            "192.168.1.5", 50000, CancellationToken.None);

        string[] expected = [CreateSession, ConnectNumeric, ConnectNotNumeric, OfferWithCandidates, OfferEmpty, Result, Accept];
        Assert.Equal(expected.Length, wire.Sent.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], wire.Sent[i].Body);
        }
    }

    // The v2 remotePlay command is the v1 body plus data3/supportCmd/protocolVer, in the captured field order.
    // The exact initialParams string (the part that reaches the console) is pinned; the outer envelope is shared
    // with v1 and already covered above.
    private const string V2InitialParams =
        "{\"accountId\":1234567890123456789, \"roomId\":0, \"sessionId\":\"s-1\", \"clientType\":\"ct\", "
        + "\"data1\":\"a\", \"data2\":\"b\",\"supportCmd\":\"120000\",\"protocolVer\":\"1.0\", \"data3\":\"c\"}";

    [Fact]
    public async Task V2RemotePlayCommand_AddsData3AndV2Markers_InCapturedOrder()
    {
        var (client, wire) = Build();
        string id = await client.SendRemotePlayCommandV2Async(
            "d-1", "1234567890123456789", "s-1", "ct", ("a", "b", "c"), CancellationToken.None);
        Assert.Equal("c-1", id);

        var (_, _, body) = Assert.Single(wire.Sent);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        string initialParams = doc.RootElement
            .GetProperty("commandDetail").GetProperty("parameters").GetProperty("initialParams").GetString()!;

        Assert.Equal(V2InitialParams, initialParams);                 // byte-exact initialParams
        Assert.Contains("\"data3\":\"c\"", initialParams);            // v1 omits data3; v2 carries it
        Assert.Contains("\"supportCmd\":\"120000\"", initialParams);  // v2 markers, captured verbatim
        Assert.Contains("\"protocolVer\":\"1.0\"", initialParams);
        Assert.DoesNotContain("\"accountId\":\"", initialParams);     // accountId stays a bare JSON number
    }

    [Fact]
    public async Task GetSession_ReadsCustomData1_WhenTheConsoleHasSealedIt()
    {
        var wire = new CustomDataWire();
        var auth = new HalyardAuthClient(new HttpClient(wire), new HalyardClientConfig("id", "secret", HalyardClientConfig.DefaultRedirectUri, HalyardClientConfig.DefaultScopes));
        var tokens = new HalyardTokenProvider(auth);
        tokens.Seed(new HalyardTokens("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        var client = new HalyardCloudClient(new HttpClient(wire), tokens);

        HalyardCloudSession session = Assert.Single(await client.GetSessionAsync("s-1", CancellationToken.None));
        Assert.Equal("double-b64-value", session.CustomData1);
    }

    private sealed class CustomDataWire : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string reply =
                """{"remotePlaySessions":[{"sessionId":"s-1","members":[{"accountId":"42","platform":"PROSPERO","deviceUniqueId":"d-1"}],"customData1":"double-b64-value"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task EveryResponse_StillReads()
    {
        var (client, _) = Build();
        HalyardAccountInfo account = await client.GetAccountInfoAsync(CancellationToken.None);
        Assert.Equal(("42", "someone", "gb"), (account.AccountId, account.OnlineId, account.Region));

        HalyardPushServerInfo push = await client.GetPushServerAsync(CancellationToken.None);
        Assert.Equal("push.example", push.Fqdn);
        Assert.Equal(4, push.KeepAlive!.ServerPresenceTimeoutMs);

        HalyardConsoleClient console = Assert.Single(await client.ListConsolesAsync(CancellationToken.None));
        Assert.Equal(("PS5-8A2F", "d-1"), (console.Device.Name, console.Duid));
        Assert.True(console.RemotePlayEnabled);

        HalyardCloudSession created = await client.CreateSessionAsync("push-ctx", CancellationToken.None);
        Assert.Equal("s-1", created.SessionId);
        Assert.Equal("d-1", Assert.Single(created.Members!).DeviceUniqueId);
        Assert.Equal("s-1", Assert.Single(await client.GetSessionsAsync(CancellationToken.None)).SessionId);
        Assert.Equal("s-1", Assert.Single(await client.GetSessionAsync("s-1", CancellationToken.None)).SessionId);
        Assert.Equal("c-1", await client.SendConnectCommandAsync("d-1", "42", "s-1", "ct", ("a", "b", "c"), CancellationToken.None));
    }
}
