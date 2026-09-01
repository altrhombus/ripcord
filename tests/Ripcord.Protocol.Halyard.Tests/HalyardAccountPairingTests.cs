using System.Security.Cryptography;
using System.Threading.Channels;
using Ripcord.Cloud.Halyard;
using Ripcord.Cloud.Halyard.Rendezvous;
using Ripcord.Core.Net.WebSockets;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Discovery;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The account ("web"/no-PIN) pairing coordinator, end to end against scripted fakes: it generates
/// <c>data1</c>/<c>data2</c>, sends the connect command, receives the console's <c>customData1</c>, recovers
/// the exact seed, and registers with it. The whole cloud → push → crypto → registration flow, no hardware.
/// </summary>
public sealed class HalyardAccountPairingTests
{
    [SkippableFact]
    public async Task PairAsync_RecoversTheSeedFromCustomData1_AndRegistersWithIt()
    {
        byte[] contextKey = BundledContextKeyOrSkip();

        // The "console": it will seal THIS seed with whatever data1/data2 the coordinator sends, and publish it.
        byte[] consoleSeed = RandomNumberGenerator.GetBytes(16);

        var socket = new ScriptedSocket();
        var signaling = new FakeSignaling(socket, consoleSeed, contextKey);
        var registration = new CapturingRegistration();

        var pairing = new HalyardAccountPairing(signaling, registration, contextKey);
        var request = new HalyardAccountPairingRequest(
            ConsoleId: "console-1", ConsoleHost: "192.168.1.50", ConsoleDuid: "duid-1",
            AccountId: "acct-1", ClientDeviceId: RandomNumberGenerator.GetBytes(16));

        await using var channel = new HalyardPushChannel(socket);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        HalyardRegistrationResult result = await pairing.PairAsync(request, channel, Server, "token", cts.Token);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotNull(registration.LastRequest);
        Assert.Equal(consoleSeed, registration.LastRequest!.AccountSeed.ToArray());   // the recovered seed drove registration
        Assert.Empty(registration.LastRequest.Passcode);                               // account route, not PIN
        Assert.True(signaling.CommandSent);                                            // data1/data2 actually went out
    }

    [SkippableFact]
    public async Task PairAsync_TimesOutCleanly_WhenNoSeedIsPublished()
    {
        byte[] contextKey = BundledContextKeyOrSkip();

        var socket = new ScriptedSocket();                       // never publishes customData1
        var signaling = new SilentSignaling();
        var registration = new CapturingRegistration();

        var pairing = new HalyardAccountPairing(signaling, registration,
            contextKey, new HalyardAccountPairingOptions { SeedTimeout = TimeSpan.FromMilliseconds(150) });
        var request = new HalyardAccountPairingRequest("c", "h", "d", "a", RandomNumberGenerator.GetBytes(16));

        await using var channel = new HalyardPushChannel(socket);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        HalyardRegistrationResult result = await pairing.PairAsync(request, channel, Server, "token", cts.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("customData1", result.FailureReason);
        Assert.Null(registration.LastRequest);                   // never reached registration
    }

    // ---- fakes ----

    private static readonly HalyardPushServerInfo Server = new(
        "abc-pushcl.np.communication.playstation.net",
        new HalyardPushKeepAlive(10_000, 40_000, 30_000, 30_000));

    private static string CustomData1Frame(string value) =>
        "{\"version\":\"2.1\",\"method\":3001,\"dataType\":\"psn:sessionManager:sys:rps:customData1:updated\","
        + "\"to\":{\"accountId\":1,\"onlineId\":\"tester\",\"platform\":[\"REMOTE_PLAY\"]},"
        + "\"body\":{\"data\":{\"customData1\":\"" + value + "\"}}}";

    /// <summary>A WebSocket whose frames are enqueued externally (by the fake signaling, on the command).</summary>
    private sealed class ScriptedSocket : IWebSocketChannel
    {
        private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();

        public void Enqueue(string frame) => _frames.Writer.TryWrite(frame);
        public void Close() => _frames.Writer.TryComplete();

        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, TimeSpan keepAlive, CancellationToken ct)
            => Task.CompletedTask;

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            try { return await _frames.Reader.ReadAsync(ct).ConfigureAwait(false); }
            catch (ChannelClosedException) { return null; }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records the connect command, then plays the console's part: seal the seed with the client's
    /// data1/data2 and publish it as customData1.</summary>
    private sealed class FakeSignaling(ScriptedSocket socket, byte[] seed, byte[] contextKey) : IHalyardSignalingClient
    {
        public bool CommandSent { get; private set; }

        public Task<string> CreateSessionAsync(string pushContextId, CancellationToken ct)
            => Task.FromResult("session-1");

        public Task SendConnectCommandAsync(
            string consoleDuid, string accountId, string sessionId, string clientType,
            (string Data1, string Data2, string Data3) seeds, CancellationToken ct)
        {
            CommandSent = true;
            byte[] data1 = Convert.FromBase64String(seeds.Data1);
            byte[] data2 = Convert.FromBase64String(seeds.Data2);
            string customData1 = HalyardAccountSeedDelivery.EncodeCustomData1(
                HalyardAccountSeedDelivery.SealSeed(data1, data2, seed, contextKey));
            socket.Enqueue(CustomData1Frame(customData1));
            socket.Close();   // let the push loop end after delivering the seed
            return Task.CompletedTask;
        }

        public Task SendOfferAsync(
            string sessionId, string accountId, string consoleDuid,
            IReadOnlyList<HalyardCandidate> candidates, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<HalyardCloudSession>> GetSessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<HalyardCloudSession>>([]);

        public Task LeaveSessionAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Creates a session and sends the command but never publishes a seed.</summary>
    private sealed class SilentSignaling : IHalyardSignalingClient
    {
        public Task<string> CreateSessionAsync(string pushContextId, CancellationToken ct) => Task.FromResult("s");
        public Task SendConnectCommandAsync(string a, string b, string c, string d, (string, string, string) e, CancellationToken ct) => Task.CompletedTask;
        public Task SendOfferAsync(string a, string b, string c, IReadOnlyList<HalyardCandidate> d, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<HalyardCloudSession>> GetSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult<IReadOnlyList<HalyardCloudSession>>([]);
        public Task LeaveSessionAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CapturingRegistration : IHalyardRegistration
    {
        public HalyardRegistrationRequest? LastRequest { get; private set; }

        public Task<HalyardRegistrationResult> RegisterAsync(HalyardRegistrationRequest request, CancellationToken ct)
        {
            LastRequest = request;
            var record = new HalyardPairingRecord([1, 2, 3, 4], [], 2);
            return Task.FromResult(new HalyardRegistrationResult(true, null, record));
        }
    }

    private static byte[] BundledContextKeyOrSkip()
    {
        var bundled = HalyardInteropConstants.Registration(HalyardConsolePlatform.Ps5);
        Skip.If(bundled is null, "Build omitted the interop constants (-p:BundleInteropConstants=false).");
        return bundled!.Value.ContextKey;
    }
}
