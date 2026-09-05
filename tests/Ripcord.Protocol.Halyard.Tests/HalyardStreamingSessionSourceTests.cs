using Ripcord.Core.Discovery;
using Ripcord.Core.Consoles;
using Ripcord.Presentation.Halyard.Sessions;
using Ripcord.Presentation.Sessions;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Which route a console takes, and why.
///
/// <para>
/// Worth testing on its own because every wrong answer here is expensive in a different way: sending a
/// same-network console out through the cloud adds tens of seconds to a connect that should be instant, and
/// sending a distant one at its last known LAN address fails after a timeout with nothing useful said. The
/// reason string is asserted too — it is shown to the user, and a route decision the UI cannot explain is how
/// a slow connect becomes a bug report.
/// </para>
/// </summary>
public class HalyardStreamingSessionSourceTests
{
    private static PairedConsole Console(string host, string? cloudId) =>
        new("console-1", "PS5-8A2F", host, "Ps5", "blob") { CloudDeviceId = cloudId };

    private static HalyardStreamingSessionSource Build(bool onThisNetwork, bool hasAccount)
        => new(
            new HalyardSessionFactory(null, new NullCredentialStore()),
            _ => throw new InvalidOperationException("These tests never open a session."),
            accountAvailable: () => hasAccount,
            cryptoSource: "test",
            isOnThisNetwork: _ => onThisNetwork);

    [Fact]
    public async Task AConsoleOnThisNetwork_TakesTheLocalRoute()
    {
        // Even with an account available: the rendezvous is strictly slower and buys nothing here.
        StreamingRouteChoice choice = await Build(onThisNetwork: true, hasAccount: true)
            .ChooseRouteAsync(Console("10.0.0.7", "duid"), CancellationToken.None);

        Assert.Equal(StreamingRoute.Local, choice.Route);
        Assert.Contains("on your network", choice.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AConsoleElsewhere_TakesTheAccountRoute()
    {
        StreamingRouteChoice choice = await Build(onThisNetwork: false, hasAccount: true)
            .ChooseRouteAsync(Console("10.0.0.7", "duid"), CancellationToken.None);

        Assert.Equal(StreamingRoute.Account, choice.Route);
        Assert.Contains("account", choice.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AConsolePairedByCode_FallsBackToLocalAndSaysWhy()
    {
        // No cloud id means the account service has no name for this console, so the rendezvous cannot ask for
        // it. Trying the last known address and failing says more than refusing up front would.
        StreamingRouteChoice choice = await Build(onThisNetwork: false, hasAccount: true)
            .ChooseRouteAsync(Console("10.0.0.7", cloudId: null), CancellationToken.None);

        Assert.Equal(StreamingRoute.Local, choice.Route);
        Assert.Contains("without an account", choice.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithNoAccountTier_FallsBackToLocalAndSaysWhy()
    {
        StreamingRouteChoice choice = await Build(onThisNetwork: false, hasAccount: false)
            .ChooseRouteAsync(Console("10.0.0.7", "duid"), CancellationToken.None);

        Assert.Equal(StreamingRoute.Local, choice.Route);
        Assert.Contains("can't connect through an account", choice.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithoutTheControlConstants_StreamingReportsUnavailableWithTheDetail()
    {
        // The page renders this verbatim instead of connecting, so it has to name what is missing.
        StreamingAvailability availability = Build(onThisNetwork: true, hasAccount: false).Availability;

        Assert.False(availability.Available);
        Assert.Contains("control-plane constants", availability.Detail);
        Assert.Contains("test", availability.Detail);
    }

    private sealed class NullCredentialStore : IConsoleCredentialStore
    {
        public Task<byte[]?> LoadAsync(string consoleId, CancellationToken cancellationToken)
            => Task.FromResult<byte[]?>(null);

        public Task SaveAsync(string consoleId, byte[] credential, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemoveAsync(string consoleId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
