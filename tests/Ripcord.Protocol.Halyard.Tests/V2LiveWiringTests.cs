using System.Net;
using System.Security.Cryptography;
using Ripcord.Core.Discovery;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The opt-in v2 live wiring: when a console's pairing selects v2 (RP-SupportGcm or a held rolled token) and the
/// session is given a v2 crypto, <c>ConnectAsync</c> runs the built v2 HTTP control handshake end-to-end and
/// stops honestly at the (not yet implemented) persistent control channel — it does not fall into the v1 field
/// crypto, which v2 throws on. Driven against the same <see cref="MockV2Console"/> the handshake tests use.
/// </summary>
public class V2LiveWiringTests
{
    [Fact]
    public async Task ConnectAsync_WithV2Pairing_RunsTheV2Handshake_AndStopsAtTheChannelBoundary()
    {
        byte[] k16 = RandomNumberGenerator.GetBytes(16);
        await using var console = new MockV2Console(k16);

        // A v2 pairing: RP-SupportGcm advertised and a rolled token held (= K16), so HalyardInitSelection picks v2.
        var pairing = new HalyardPairingRecord(
            RegistrationKey: "6c9b1c31"u8.ToArray(), Companion: new byte[16], KeyType: 0, Platform: HalyardConsolePlatform.Ps5)
        {
            RolledToken = k16,
            SupportsGcm = true,
        };
        var store = new InMemoryStore(pairing.Serialize());

        var parameters = new HalyardConnectionParameters(
            "console-1",
            new IPEndPoint(IPAddress.Loopback, 9295),
            new IPEndPoint(IPAddress.Loopback, 9296),
            DeviceId: new byte[16],
            ConnectionPath: HalyardConnectionPath.Rendezvous); // non-Local so no UDP probe runs

        await using var session = new HalyardStreamingSession(
            parameters, console, new PassthroughHalyardSessionCrypto(), store,
            v2Crypto: new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets()));

        var config = new SessionConfig(1920, 1080, 60, 10_000, VideoCodec.H264, LatencyMode.Balanced);
        SessionHandshakeResult result = await session.ConnectAsync(config, CancellationToken.None);

        // The v2 handshake ran (the mock received our init2 verifyKey) and the session stopped at the honest
        // boundary rather than crashing into the v1 field crypto.
        Assert.NotNull(console.ReceivedVerifyKey);
        Assert.False(result.Succeeded);
        Assert.Contains("v2 control handshake completed", result.FailureReason);
    }

    [Fact]
    public async Task ConnectAsync_WithoutV2Crypto_DoesNotTakeTheV2Branch()
    {
        // No v2 crypto injected (the factory opt-in is off): even a v2-capable pairing must not run the v2
        // handshake — the session stays on the v1 path. The mock would record a verifyKey if init2 ran; it must not.
        byte[] k16 = RandomNumberGenerator.GetBytes(16);
        await using var console = new MockV2Console(k16);
        var pairing = new HalyardPairingRecord(
            RegistrationKey: "6c9b1c31"u8.ToArray(), Companion: new byte[16], KeyType: 0, Platform: HalyardConsolePlatform.Ps5)
        {
            RolledToken = k16,
            SupportsGcm = true,
        };
        var store = new InMemoryStore(pairing.Serialize());
        var parameters = new HalyardConnectionParameters(
            "console-1",
            new IPEndPoint(IPAddress.Loopback, 9295),
            new IPEndPoint(IPAddress.Loopback, 9296),
            DeviceId: new byte[16],
            ConnectionPath: HalyardConnectionPath.Rendezvous);

        await using var session = new HalyardStreamingSession(
            parameters, console, new PassthroughHalyardSessionCrypto(), store); // v2Crypto left null

        var config = new SessionConfig(1920, 1080, 60, 10_000, VideoCodec.H264, LatencyMode.Balanced);
        SessionHandshakeResult result = await session.ConnectAsync(config, CancellationToken.None);

        Assert.Null(console.ReceivedVerifyKey);                      // the v2 handshake never ran
        Assert.DoesNotContain("v2 control handshake", result.FailureReason ?? ""); // v1 path, whatever its outcome
    }

    private sealed class InMemoryStore(byte[] blob) : IConsoleCredentialStore
    {
        private readonly byte[] _blob = blob;
        public Task<byte[]?> LoadAsync(string consoleId, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(_blob);
        public Task SaveAsync(string consoleId, byte[] credential, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string consoleId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
