using System.Security.Cryptography;
using System.Threading.Channels;
using Ripcord.Cloud.Halyard;
using Ripcord.Core.Net.WebSockets;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The push channel's dispatch loop — driven by a fake WebSocket so the whole path (connect, receive, parse,
/// surface, close, cancel) runs without a live server.
///
/// <para>
/// The payoff of the <see cref="IWebSocketChannel"/> seam: <see cref="RealCapturedFrames_SurfaceTheConsoleOffer"/>
/// replays the actual captured push frames through the channel and asserts a console OFFER with candidates
/// comes out — end-to-end proof on ground truth, no hardware.
/// </para>
/// </summary>
public class HalyardPushChannelTests
{
    /// <summary>A scripted WebSocket: hands out queued frames, then null (closed).</summary>
    private sealed class FakeWebSocket : IWebSocketChannel
    {
        private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();

        public bool Connected { get; private set; }

        public Uri? ConnectedUri { get; private set; }

        public IReadOnlyDictionary<string, string>? Headers { get; private set; }

        public TimeSpan KeepAlive { get; private set; }

        public void Enqueue(params string[] frames)
        {
            foreach (string f in frames)
            {
                _frames.Writer.TryWrite(f);
            }
        }

        /// <summary>Signal the peer closing the connection (a null receive).</summary>
        public void Close() => _frames.Writer.TryComplete();

        public Task ConnectAsync(
            Uri uri, IReadOnlyDictionary<string, string> headers, TimeSpan keepAliveInterval, CancellationToken ct)
        {
            Connected = true;
            ConnectedUri = uri;
            Headers = headers;
            KeepAlive = keepAliveInterval;
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            try
            {
                return await _frames.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null; // peer closed
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly HalyardPushServerInfo Server = new(
        "abc-pushcl.np.communication.playstation.net",
        new HalyardPushKeepAlive(10_000, 40_000, 30_000, 30_000));

    private static string ConsoleOfferFrame =>
        "{\"version\":\"2.1\",\"method\":3001,"
        + "\"dataType\":\"psn:sessionManager:sys:rps:sessionMessage:created\","
        + "\"to\":{\"accountId\":1,\"onlineId\":\"tester\",\"platform\":[\"REMOTE_PLAY\"]},"
        + "\"body\":{\"data\":{"
        + "\"customProperties\":{\"from\":{\"accountId\":\"1\",\"platform\":\"PROSPERO\"}},"
        + "\"sessionId\":\"00000000-0000-0000-0000-000000000000\","
        + "\"sessionMessage\":{\"channel\":\"remote_play:1\","
        + "\"payload\":\"ver=1.0, type=text, body="
        + "{\\\"action\\\":\\\"OFFER\\\",\\\"reqId\\\":1,\\\"error\\\":0,\\\"connRequest\\\":{"
        + "\\\"skey\\\":\\\"3DlNoSVIIrMt0LZJUsljaQ==\\\",\\\"natType\\\":2,\\\"candidate\\\":["
        + "{\\\"type\\\":\\\"STATIC\\\",\\\"addr\\\":\\\"203.0.113.7\\\",\\\"mappedAddr\\\":\\\"0.0.0.0\\\",\\\"port\\\":9303,\\\"mappedPort\\\":0}],"
        + "\\\"localHashedId\\\":\\\"kZ4dwoBU+W1wDpBeejemC+lZ1Eo=\\\"}}\"}}}}";

    private const string PresenceFrame =
        "{\"version\":\"2.1\",\"method\":3001,\"dataType\":\"psn:sessionManager:sys:rps:members:created\","
        + "\"to\":{\"accountId\":1,\"onlineId\":\"tester\",\"platform\":[\"REMOTE_PLAY\"]},\"body\":{\"data\":{}}}";

    private static string CustomData1Frame(string value) =>
        "{\"version\":\"2.1\",\"method\":3001,\"dataType\":\"psn:sessionManager:sys:rps:customData1:updated\","
        + "\"to\":{\"accountId\":1,\"onlineId\":\"tester\",\"platform\":[\"REMOTE_PLAY\"]},"
        + "\"body\":{\"data\":{\"customData1\":\"" + value + "\"}}}";

    private static async Task<List<HalyardSignalingMessage>> RunAsync(FakeWebSocket socket)
    {
        var received = new List<HalyardSignalingMessage>();
        await using var channel = new HalyardPushChannel(socket);
        channel.SignalingReceived += received.Add;

        // The socket closes itself (Close), so RunAsync returns on its own; a timeout guards against a hang.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(Server, "test-token", cts.Token);
        return received;
    }

    [Fact]
    public async Task ConnectsToThePushUriWithBearerAuthAndKeepalive()
    {
        var socket = new FakeWebSocket();
        socket.Close();

        await RunAsync(socket);

        Assert.True(socket.Connected);
        Assert.Equal("wss://abc-pushcl.np.communication.playstation.net/np/pushNotification", socket.ConnectedUri!.ToString());
        Assert.Equal("Bearer test-token", socket.Headers!["Authorization"]);
        Assert.Equal(TimeSpan.FromSeconds(10), socket.KeepAlive);
    }

    [Fact]
    public async Task SendsTheRequiredUpgradeHeadersAndSubprotocol()
    {
        // Matched to the captured vendor handshake (cap68). Omitting the np-pushpacket subprotocol was the 400;
        // these pin the whole set so it cannot silently regress.
        var socket = new FakeWebSocket();
        socket.Close();

        await RunAsync(socket);

        Assert.Equal("np-pushpacket", socket.Headers!["Sec-WebSocket-Protocol"]);
        Assert.Equal("REMOTE_PLAY", socket.Headers["X-PSN-APP-TYPE"]);
        Assert.Equal("RemotePlay/1.0", socket.Headers["X-PSN-APP-VER"]);
        Assert.Equal("2.1", socket.Headers["X-PSN-PROTOCOL-VERSION"]);
        Assert.Equal("3", socket.Headers["X-PSN-KEEP-ALIVE-STATUS-TYPE"]);
        Assert.Equal("false", socket.Headers["X-PSN-RECONNECTION"]);
        Assert.Contains("X-PSN-OS-VER", socket.Headers.Keys);
        Assert.Contains("User-Agent", socket.Headers.Keys);
    }

    [Fact]
    public async Task SurfacesSignalingFramesAndDropsPresenceNotifications()
    {
        var socket = new FakeWebSocket();
        socket.Enqueue(PresenceFrame, ConsoleOfferFrame, PresenceFrame);
        socket.Close();

        List<HalyardSignalingMessage> received = await RunAsync(socket);

        HalyardSignalingMessage offer = Assert.Single(received);
        Assert.True(offer.IsConsoleOffer);
        Assert.Equal("PROSPERO", offer.FromPlatform);
        Assert.Contains(offer.Candidates, c => c.Type == "STATIC" && c.Port == 9303);
    }

    [Fact]
    public async Task SurfacesCustomData1_AndNotAsSignaling()
    {
        // customData1 (the encrypted account-registration seed) rides the same channel; it is surfaced on its
        // own event, never mistaken for a signaling OFFER, and non-customData frames raise nothing.
        var socket = new FakeWebSocket();
        socket.Enqueue(PresenceFrame, CustomData1Frame("bTNadUEzNy9NcGFic1k5"), ConsoleOfferFrame);
        socket.Close();

        var signaling = new List<HalyardSignalingMessage>();
        var customData = new List<string>();
        await using var channel = new HalyardPushChannel(socket);
        channel.SignalingReceived += signaling.Add;
        channel.CustomData1Received += customData.Add;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(Server, "test-token", cts.Token);

        Assert.Equal("bTNadUEzNy9NcGFic1k5", Assert.Single(customData));
        Assert.True(Assert.Single(signaling).IsConsoleOffer);   // the OFFER still comes through, on its own event
    }

    [SkippableFact]
    public async Task DeliversTheAccountRegistrationSeed_EndToEnd()
    {
        // Proves the whole account seed-delivery path across the layer seam: the console seals a seed with the
        // client's data1/data2, publishes it as customData1, and the client recovers exactly that seed — over
        // the real push-channel dispatch, no live server.
        var bundled = HalyardInteropConstants.Registration(HalyardConsolePlatform.Ps5);
        Skip.If(bundled is null, "Build omitted the interop constants (-p:BundleInteropConstants=false).");
        byte[] contextKey = bundled!.Value.ContextKey;

        var (data1, data2) = HalyardAccountSeedDelivery.GenerateEphemeralKeyMaterial();
        byte[] seed = RandomNumberGenerator.GetBytes(16);
        string customData1 = HalyardAccountSeedDelivery.EncodeCustomData1(
            HalyardAccountSeedDelivery.SealSeed(data1, data2, seed, contextKey));

        var socket = new FakeWebSocket();
        socket.Enqueue(CustomData1Frame(customData1));
        socket.Close();

        byte[]? recovered = null;
        await using var channel = new HalyardPushChannel(socket);
        channel.CustomData1Received += value =>
            recovered = HalyardAccountSeedDelivery.RecoverSeed(data1, data2, value, contextKey);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(Server, "test-token", cts.Token);

        Assert.Equal(seed, recovered);
    }

    [Fact]
    public async Task AThrowingConsumerDoesNotKillTheConnection()
    {
        // A frame after the one that throws must still be delivered — a bad handler cannot take down the
        // whole connect attempt.
        var socket = new FakeWebSocket();
        socket.Enqueue(ConsoleOfferFrame, ConsoleOfferFrame);
        socket.Close();

        int delivered = 0;
        await using var channel = new HalyardPushChannel(socket);
        channel.SignalingReceived += _ =>
        {
            delivered++;
            throw new InvalidOperationException("bad consumer");
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(Server, "test-token", cts.Token);

        Assert.Equal(2, delivered);
    }

    [Fact]
    public async Task ReturnsWhenThePeerCloses()
    {
        // A null receive (the 4101 teardown, or a normal close) ends RunAsync rather than looping forever.
        var socket = new FakeWebSocket();
        socket.Enqueue(ConsoleOfferFrame);
        socket.Close();

        // No external cancellation — RunAsync must return on the close alone.
        var received = new List<HalyardSignalingMessage>();
        await using var channel = new HalyardPushChannel(socket);
        channel.SignalingReceived += received.Add;

        Task run = channel.RunAsync(Server, "test-token", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(run.IsCompletedSuccessfully);
        Assert.Single(received);
    }

    [Fact]
    public async Task StopsWhenCancelled_WhileTheConnectionIsStillOpen()
    {
        // A live push connection never closes on its own during a session; cancellation is how the client
        // tears it down. The socket here never closes, so only the token can end the loop.
        var socket = new FakeWebSocket();
        socket.Enqueue(ConsoleOfferFrame);

        var received = new List<HalyardSignalingMessage>();
        await using var channel = new HalyardPushChannel(socket);
        channel.SignalingReceived += received.Add;

        using var cts = new CancellationTokenSource();
        Task run = channel.RunAsync(Server, "test-token", cts.Token);

        // Let the queued frame drain, then cancel.
        await Task.Delay(100);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(received);
    }

    [Fact]
    public void PushServerInfo_FallsBackToTenSecondKeepaliveWhenUnspecified()
    {
        var info = new HalyardPushServerInfo("host", KeepAlive: null);

        Assert.Equal(TimeSpan.FromSeconds(10), info.ClientKeepAlive);
        Assert.Equal("wss://host/np/pushNotification", info.PushUri.ToString());
    }
}
