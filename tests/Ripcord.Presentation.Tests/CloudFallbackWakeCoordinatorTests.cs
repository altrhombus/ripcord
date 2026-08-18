using Ripcord.Core.Consoles;
using Ripcord.Presentation.Accounts;
using Ripcord.Presentation.Sessions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The "and if the local wake couldn't reach it, ask the account service" policy.
///
/// <para>
/// The interesting cases are all about restraint. The remote wake costs a round trip and can only be reported
/// as unverified, so it must fire in exactly one situation — a console the local broadcast could not reach — and
/// stay out of the way in every other, including when it fails.
/// </para>
/// </summary>
public class CloudFallbackWakeCoordinatorTests
{
    private sealed class StubLocal(ConsoleWakeOutcome outcome) : IConsoleWakeCoordinator
    {
        public int Calls { get; private set; }

        public Task<ConsoleWakeOutcome> EnsureAwakeAsync(
            PairedConsole console, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(outcome);
        }
    }

    private static PairedConsole Console(string? cloudDeviceId = "duid-1")
        => new("id", "PlayStation 5", "10.0.0.7", "PS5", "blob") { CloudDeviceId = cloudDeviceId };

    private static FakeAccountSession SignedIn()
        => new() { Current = new AccountIdentity("42", "somebody", "GB") };

    private static async Task<(ConsoleWakeOutcome Outcome, FakeAccountSession Account, StubLocal Local)> RunAsync(
        ConsoleWakeOutcome localOutcome,
        PairedConsole? console = null,
        FakeAccountSession? account = null)
    {
        var local = new StubLocal(localOutcome);
        FakeAccountSession session = account ?? SignedIn();
        var coordinator = new CloudFallbackWakeCoordinator(local, session);

        ConsoleWakeOutcome outcome = await coordinator.EnsureAwakeAsync(
            console ?? Console(), progress: null, CancellationToken.None);

        return (outcome, session, local);
    }

    [Theory]
    [InlineData(ConsoleWakeOutcome.AlreadyAwake)]
    [InlineData(ConsoleWakeOutcome.Woken)]
    [InlineData(ConsoleWakeOutcome.TimedOut)]
    public async Task WhenTheLocalPathSettledIt_TheCloudIsNotAsked(ConsoleWakeOutcome localOutcome)
    {
        // Including TimedOut: a console we can see but that refuses to come up is not helped by asking PSN,
        // and doing so would add a round trip to every failed wake.
        var (outcome, account, _) = await RunAsync(localOutcome);

        Assert.Equal(localOutcome, outcome);
        Assert.Empty(account.WakeRequests);
    }

    [Fact]
    public async Task WhenTheLocalPathCouldNotReachIt_TheCloudIsAsked()
    {
        // Unknown from the local coordinator covers "did not answer discovery at all", which is exactly the
        // off-network console this fallback exists for.
        var (outcome, account, _) = await RunAsync(ConsoleWakeOutcome.Unknown);

        Assert.Equal(ConsoleWakeOutcome.AskedRemotely, outcome);
        Assert.Equal(["duid-1"], account.WakeRequests);
    }

    [Fact]
    public async Task TheRemoteResultIsReportedAsUnverified()
    {
        // Not Woken. Acceptance means the request was queued, and claiming more than that is the kind of
        // over-confident status string this app has lost hours to before.
        var (outcome, _, _) = await RunAsync(ConsoleWakeOutcome.Unknown);

        Assert.NotEqual(ConsoleWakeOutcome.Woken, outcome);
        Assert.Equal(ConsoleWakeOutcome.AskedRemotely, outcome);
    }

    [Fact]
    public async Task WithoutACloudDeviceId_TheCloudIsNotAsked()
    {
        // A console paired while signed out. We do not know how the account service addresses it.
        var (outcome, account, _) = await RunAsync(ConsoleWakeOutcome.Unknown, Console(cloudDeviceId: null));

        Assert.Equal(ConsoleWakeOutcome.Unknown, outcome);
        Assert.Empty(account.WakeRequests);
    }

    [Fact]
    public async Task WhenSignedOut_TheCloudIsNotAsked()
    {
        var (outcome, account, _) = await RunAsync(
            ConsoleWakeOutcome.Unknown, account: new FakeAccountSession { Current = null });

        Assert.Equal(ConsoleWakeOutcome.Unknown, outcome);
        Assert.Empty(account.WakeRequests);
    }

    [Fact]
    public async Task WhenTheRemoteWakeFails_TheLocalOutcomeSurvivesAndNothingThrows()
    {
        // This is a bonus attempt on a path that had already failed. Its failure must not make things worse.
        FakeAccountSession account = SignedIn();
        account.WakeThrows = new InvalidOperationException("cloud down");

        var (outcome, _, _) = await RunAsync(ConsoleWakeOutcome.Unknown, account: account);

        Assert.Equal(ConsoleWakeOutcome.Unknown, outcome);
    }

    [Fact]
    public async Task TheLocalPathIsAlwaysTriedFirst()
    {
        // Faster, needs no internet, and unlike the remote one it can actually confirm the console came up.
        var (_, _, local) = await RunAsync(ConsoleWakeOutcome.AlreadyAwake);

        Assert.Equal(1, local.Calls);
    }

    [Fact]
    public async Task CancellationIsNotSwallowedByTheFailureGuard()
    {
        FakeAccountSession account = SignedIn();
        account.WakeThrows = new OperationCanceledException();
        var coordinator = new CloudFallbackWakeCoordinator(new StubLocal(ConsoleWakeOutcome.Unknown), account);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => coordinator.EnsureAwakeAsync(Console(), null, CancellationToken.None));
    }
}
