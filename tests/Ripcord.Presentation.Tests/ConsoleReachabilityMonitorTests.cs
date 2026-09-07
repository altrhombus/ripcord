using Ripcord.Core.Consoles;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Reachability probing and the rest-settle watch.
///
/// <para>
/// None of this was testable while it lived in a page's code-behind: exercising it meant launching the app
/// against real consoles, and the rest watch in particular meant waiting a real minute to see what happens when
/// its budget runs out. Both the concurrency claim ("one offline console must not delay the others") and the
/// budget were therefore assertions nobody had ever checked.
/// </para>
/// </summary>
public class ConsoleReachabilityMonitorTests
{
    private static PairedConsole Console(string host, string platform = "Ps5") =>
        new(Id: host, Name: "PlayStation 5", Host: host, Platform: platform, CredentialBlob: "");

    private static PairedConsole CloudConsole(string host, string cloudId) =>
        Console(host) with { CloudDeviceId = cloudId };

    // ---- silence means "offline" only on this network ---------------------------------------------

    [Fact]
    public async Task AConsoleThatIsSilentButListedByTheAccount_IsAwayNotOffline()
    {
        // The whole point of the distinction. A discovery probe cannot leave the subnet, so silence from a
        // console elsewhere says nothing about it -- and calling it Offline made it unconnectable from the app
        // while the account route could reach it perfectly well.
        var card = new ConsoleCardViewModel(CloudConsole("10.0.0.5", "duid-1"), new ImmediateUiDispatcher());
        var monitor = new ConsoleReachabilityMonitor(
            new FakeProbe().Answers("10.0.0.5", (bool?)null),
            remotelyAvailable: _ => Task.FromResult<IReadOnlyCollection<string>>(["duid-1"]));

        await monitor.RefreshAsync([card], restRequestedHost: null, CancellationToken.None);

        Assert.Equal(ConsoleReachability.Away, card.Reachability);
        Assert.True(card.State.CanConnect);
    }

    [Fact]
    public async Task AConsoleTheAccountDoesNotList_StaysOffline()
    {
        // Listed for the account but not this console: it really is unreachable, and saying otherwise would
        // promise a connect that fails.
        var card = new ConsoleCardViewModel(CloudConsole("10.0.0.5", "duid-1"), new ImmediateUiDispatcher());
        var monitor = new ConsoleReachabilityMonitor(
            new FakeProbe().Answers("10.0.0.5", (bool?)null),
            remotelyAvailable: _ => Task.FromResult<IReadOnlyCollection<string>>(["duid-other"]));

        await monitor.RefreshAsync([card], restRequestedHost: null, CancellationToken.None);

        Assert.Equal(ConsoleReachability.Offline, card.Reachability);
        Assert.False(card.State.CanConnect);
    }

    [Fact]
    public async Task AConsolePairedByCode_IsNotEvenAskedAbout()
    {
        // No cloud id means the account service has no name for it, so there is nothing to ask -- and asking
        // would spend a REST call to learn nothing.
        var asked = false;
        var card = Card("10.0.0.5");
        var monitor = new ConsoleReachabilityMonitor(
            new FakeProbe().Answers("10.0.0.5", (bool?)null),
            remotelyAvailable: _ =>
            {
                asked = true;
                return Task.FromResult<IReadOnlyCollection<string>>([]);
            });

        await monitor.RefreshAsync([card], restRequestedHost: null, CancellationToken.None);

        Assert.Equal(ConsoleReachability.Offline, card.Reachability);
        Assert.False(asked);
    }

    [Fact]
    public async Task AConsoleThatAnswersLocally_NeverCostsAnAccountCall()
    {
        // The common case, and it must stay free: everything on this network answers, so the list is never
        // fetched at all.
        var asked = 0;
        var monitor = new ConsoleReachabilityMonitor(
            new FakeProbe().Answers("10.0.0.5", true).Answers("10.0.0.6", false),
            remotelyAvailable: _ =>
            {
                asked++;
                return Task.FromResult<IReadOnlyCollection<string>>([]);
            });

        await monitor.RefreshAsync(
            [Card("10.0.0.5"), Card("10.0.0.6")], restRequestedHost: null, CancellationToken.None);

        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task SeveralSilentConsoles_ShareOneAccountCall()
    {
        // One call for the list, not one per card -- which is the reason this takes a function and not the
        // account seam itself.
        var asked = 0;
        var monitor = new ConsoleReachabilityMonitor(
            new FakeProbe().Answers("10.0.0.5", (bool?)null).Answers("10.0.0.6", (bool?)null),
            remotelyAvailable: _ =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<IReadOnlyCollection<string>>(["duid-1", "duid-2"]);
            });

        var first = new ConsoleCardViewModel(CloudConsole("10.0.0.5", "duid-1"), new ImmediateUiDispatcher());
        var second = new ConsoleCardViewModel(CloudConsole("10.0.0.6", "duid-2"), new ImmediateUiDispatcher());

        await monitor.RefreshAsync([first, second], restRequestedHost: null, CancellationToken.None);

        Assert.Equal(ConsoleReachability.Away, first.Reachability);
        Assert.Equal(ConsoleReachability.Away, second.Reachability);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task WhenAskingTheAccountFails_TheConsoleIsOfflineRatherThanAnError()
    {
        // A status dot is not worth surfacing an error over, and offline is what the user would have seen
        // before there was an account tier at all.
        var card = new ConsoleCardViewModel(CloudConsole("10.0.0.5", "duid-1"), new ImmediateUiDispatcher());
        var monitor = new ConsoleReachabilityMonitor(
            new FakeProbe().Answers("10.0.0.5", (bool?)null),
            remotelyAvailable: _ => Task.FromException<IReadOnlyCollection<string>>(
                new InvalidOperationException("the account service is unreachable")));

        await monitor.RefreshAsync([card], restRequestedHost: null, CancellationToken.None);

        Assert.Equal(ConsoleReachability.Offline, card.Reachability);
    }

    [Fact]
    public async Task WithNoAccountTier_SilenceIsOfflineExactlyAsBefore()
    {
        var card = new ConsoleCardViewModel(CloudConsole("10.0.0.5", "duid-1"), new ImmediateUiDispatcher());
        var monitor = new ConsoleReachabilityMonitor(new FakeProbe().Answers("10.0.0.5", (bool?)null));

        await monitor.RefreshAsync([card], restRequestedHost: null, CancellationToken.None);

        Assert.Equal(ConsoleReachability.Offline, card.Reachability);
    }

    private static ConsoleCardViewModel Card(string host, string platform = "Ps5")
        => new(Console(host, platform), new ImmediateUiDispatcher());

    /// <summary>A probe with a scripted answer per host, and optional gates to control ordering.</summary>
    private sealed class FakeProbe : IConsoleReachabilityProbe
    {
        private readonly Dictionary<string, Queue<bool?>> _answers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Probed { get; } = [];

        public Func<PairedConsole, Exception?>? Throws { get; set; }

        /// <summary>Queue the answers this host will give, in order. The last one repeats.</summary>
        public FakeProbe Answers(string host, params bool?[] answers)
        {
            // Fail loudly rather than binding a bare null to the array and silently scripting nothing.
            ArgumentNullException.ThrowIfNull(answers);
            _answers[host] = new Queue<bool?>(answers);
            return this;
        }

        /// <summary>Make this host's probe block until <see cref="Release"/>.</summary>
        public FakeProbe Gate(string host)
        {
            _gates[host] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return this;
        }

        public void Release(string host) => _gates[host].TrySetResult();

        public async Task<bool?> ProbeAsync(PairedConsole console, CancellationToken cancellationToken)
        {
            lock (Probed)
            {
                Probed.Add(console.Host);
            }

            if (Throws?.Invoke(console) is { } ex)
            {
                throw ex;
            }

            if (_gates.TryGetValue(console.Host, out TaskCompletionSource? gate))
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!_answers.TryGetValue(console.Host, out Queue<bool?>? queue) || queue.Count == 0)
            {
                return null;
            }

            return queue.Count == 1 ? queue.Peek() : queue.Dequeue();
        }
    }

    /// <summary>Records the delays asked for and completes them instantly, so the budget is testable in microseconds.</summary>
    private sealed class RecordingDelay
    {
        public List<TimeSpan> Requested { get; } = [];

        public Task Delay(TimeSpan span, CancellationToken cancellationToken)
        {
            Requested.Add(span);
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : Task.CompletedTask;
        }
    }

    private static ConsoleReachabilityMonitor NewMonitor(
        FakeProbe probe, RecordingDelay? delay = null, int? maxChecks = null)
        => new(probe, (delay ?? new RecordingDelay()).Delay, restSettleMaxChecks: maxChecks);

    // ---- single probe classification -------------------------------------------------------------

    [Theory]
    [InlineData(true, ConsoleReachability.Online)]
    [InlineData(false, ConsoleReachability.Resting)]
    [InlineData(null, ConsoleReachability.Offline)]
    public async Task Probe_ClassifiesTheAnswer(bool? awake, ConsoleReachability expected)
    {
        var probe = new FakeProbe().Answers("10.0.0.7", awake);
        var card = Card("10.0.0.7");

        await NewMonitor(probe).RefreshAsync([card], restRequestedHost: null, CancellationToken.None);

        Assert.Equal(expected, card.Reachability);
    }

    [Fact]
    public async Task Probe_ThatThrows_LeavesTheCardCheckingRatherThanClaimingOffline()
    {
        // A probe that blew up has told us nothing. Reporting "Offline" would be inventing a fact.
        var probe = new FakeProbe { Throws = _ => new OperationCanceledException() };
        var card = Card("10.0.0.7");

        await NewMonitor(probe).RefreshAsync([card], restRequestedHost: null, CancellationToken.None);

        Assert.Equal(ConsoleReachability.Checking, card.Reachability);
    }

    [Fact]
    public async Task Probe_UsesTheConsolesOwnRecord_SoFamilyIsPreserved()
    {
        // The probe needs the record, not just an address: a PS4 answers on a different port and version, and
        // probing it as a PS5 would report a healthy console as offline.
        var probe = new FakeProbe().Answers("10.0.0.9", true);
        var ps4 = Card("10.0.0.9", platform: "Ps4");

        await NewMonitor(probe).RefreshAsync([ps4], restRequestedHost: null, CancellationToken.None);

        Assert.Equal(ConsoleReachability.Online, ps4.Reachability);
    }

    // ---- concurrency ----------------------------------------------------------------------------

    [Fact]
    public async Task Probes_RunConcurrently_SoOneSlowConsoleDoesNotDelayTheOthers()
    {
        // The claim the original code's comment made and nothing verified.
        // (bool?)null, not a bare null: a bare null binds to the params ARRAY rather than to one element.
        var probe = new FakeProbe().Answers("slow", (bool?)null).Answers("fast", true).Gate("slow");
        var slow = Card("slow");
        var fast = Card("fast");

        Task refresh = NewMonitor(probe).RefreshAsync([slow, fast], null, CancellationToken.None);

        // The fast console has already resolved while the slow one is still outstanding.
        Assert.Equal(ConsoleReachability.Online, fast.Reachability);
        Assert.Equal(ConsoleReachability.Checking, slow.Reachability);
        Assert.False(refresh.IsCompleted);

        probe.Release("slow");
        await refresh;

        Assert.Equal(ConsoleReachability.Offline, slow.Reachability);
    }

    [Fact]
    public async Task Cancellation_LeavesRowsAlone_RatherThanResolvingOntoARebuiltList()
    {
        var probe = new FakeProbe().Answers("10.0.0.7", true).Gate("10.0.0.7");
        var card = Card("10.0.0.7");
        using var cts = new CancellationTokenSource();

        Task refresh = NewMonitor(probe).RefreshAsync([card], null, cts.Token);
        cts.Cancel();
        probe.Release("10.0.0.7");
        await refresh;

        Assert.Equal(ConsoleReachability.Checking, card.Reachability);
    }

    // ---- the rest-settle watch ------------------------------------------------------------------

    [Fact]
    public async Task RestWatch_ShowsTheTransitionImmediately()
    {
        // Before any delay elapses: the user has just asked for rest and the card must say so at once.
        var probe = new FakeProbe().Answers("10.0.0.7", true).Gate("10.0.0.7");
        var card = Card("10.0.0.7");

        Task refresh = NewMonitor(probe).RefreshAsync([card], "10.0.0.7", CancellationToken.None);
        Assert.Equal(ConsoleReachability.PreparingForRest, card.Reachability);

        probe.Release("10.0.0.7");
        await refresh;
    }

    [Fact]
    public async Task RestWatch_SettlesEarlyAndStopsProbing_OnTheFirstStandbyAnswer()
    {
        var probe = new FakeProbe().Answers("10.0.0.7", false);
        var delay = new RecordingDelay();
        var card = Card("10.0.0.7");

        await NewMonitor(probe, delay).RefreshAsync([card], "10.0.0.7", CancellationToken.None);

        Assert.Equal(ConsoleReachability.Resting, card.Reachability);
        Assert.Single(probe.Probed);          // stopped early rather than burning the budget
        Assert.Single(delay.Requested);
    }

    [Fact]
    public async Task RestWatch_KeepsWaitingWhileTheConsoleStillAnswersAwake()
    {
        // Online mid-transition means it has not gone down yet, not that rest failed.
        var probe = new FakeProbe().Answers("10.0.0.7", true, true, false);
        var card = Card("10.0.0.7");

        await NewMonitor(probe).RefreshAsync([card], "10.0.0.7", CancellationToken.None);

        Assert.Equal(ConsoleReachability.Resting, card.Reachability);
        Assert.Equal(3, probe.Probed.Count);
    }

    [Fact]
    public async Task RestWatch_TreatsATransientNoReplyAsStillSettling()
    {
        // A bare no-reply can be a momentary gap mid-transition, so it must not end the watch early with
        // "Offline" when the very next probe would have reported clean rest.
        var probe = new FakeProbe().Answers("10.0.0.7", null, false);
        var card = Card("10.0.0.7");

        await NewMonitor(probe).RefreshAsync([card], "10.0.0.7", CancellationToken.None);

        Assert.Equal(ConsoleReachability.Resting, card.Reachability);
    }

    [Fact]
    public async Task RestWatch_BudgetExhausted_ReportsTheLastThingItActuallySaw()
    {
        // The console never rested. Leaving the card stuck on "Going to sleep…" forever would be worse than
        // telling the truth, so the last observed state wins.
        var probe = new FakeProbe().Answers("10.0.0.7", true);
        var delay = new RecordingDelay();
        var card = Card("10.0.0.7");

        await NewMonitor(probe, delay).RefreshAsync([card], "10.0.0.7", CancellationToken.None);

        Assert.Equal(ConsoleReachability.Online, card.Reachability);
        Assert.Equal(ConsoleReachabilityMonitor.DefaultRestSettleMaxChecks, probe.Probed.Count);
        Assert.All(delay.Requested, d => Assert.Equal(ConsoleReachabilityMonitor.DefaultRestSettleInterval, d));
    }

    [Fact]
    public async Task RestWatch_IsBounded_SoItCanNeverBecomeABackgroundPoll()
    {
        var probe = new FakeProbe().Answers("10.0.0.7", true);
        var delay = new RecordingDelay();

        await NewMonitor(probe, delay, maxChecks: 3)
            .RefreshAsync([Card("10.0.0.7")], "10.0.0.7", CancellationToken.None);

        Assert.Equal(3, delay.Requested.Count);
    }

    [Fact]
    public async Task RestWatch_AppliesOnlyToTheRestRequestedConsole()
    {
        var probe = new FakeProbe().Answers("rested", false).Answers("other", true);
        var delay = new RecordingDelay();
        var rested = Card("rested");
        var other = Card("other");

        await NewMonitor(probe, delay).RefreshAsync([rested, other], "rested", CancellationToken.None);

        Assert.Equal(ConsoleReachability.Resting, rested.Reachability);
        Assert.Equal(ConsoleReachability.Online, other.Reachability);

        // Exactly one delay: the watched console's. The other took the single-probe path.
        Assert.Single(delay.Requested);
    }

    [Fact]
    public async Task RestWatch_MatchesTheHostCaseInsensitively()
    {
        var probe = new FakeProbe().Answers("Console.Local", false);
        var card = Card("Console.Local");

        await NewMonitor(probe).RefreshAsync([card], "console.local", CancellationToken.None);

        Assert.Equal(ConsoleReachability.Resting, card.Reachability);
    }

    [Fact]
    public async Task Refresh_WithNoConsoles_IsANoOp()
    {
        var probe = new FakeProbe();
        await NewMonitor(probe).RefreshAsync([], null, CancellationToken.None);
        Assert.Empty(probe.Probed);
    }

    [Fact]
    public void Constructor_RejectsANullProbe()
        => Assert.Throws<ArgumentNullException>(() => new ConsoleReachabilityMonitor(null!));
}
