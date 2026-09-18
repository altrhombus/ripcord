using Ripcord.Protocol.Halyard.Common.Discovery;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The wake decision logic, driven by fake I/O so no socket is opened. Intervals are tiny so the timed path
/// resolves in milliseconds.
/// </summary>
public class WakeCoordinatorTests
{
    // The probe repeats its LAST scripted value once the queue drains, rather than defaulting to a fixed
    // answer. The timeout test cannot enumerate every poll a fast machine fits into the budget, so a queue
    // that runs out to "awake" made the test flaky — it would report Woke under load. Repeating the last
    // value keeps the intended sequence stable however many times the coordinator polls.
    // Named rather than inline, because one test now asserts the arithmetic between them.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan WakeBudget = TimeSpan.FromMilliseconds(50);

    private static HalyardWakeCoordinator Build(IReadOnlyList<bool?> script, Action onWake, Action? onProbe = null)
    {
        int i = 0;
        return new(
            probeAwake: _ =>
            {
                onProbe?.Invoke();
                bool? value = script[Math.Min(i, script.Count - 1)];
                i++;
                return Task.FromResult(value);
            },
            sendWake: _ => { onWake(); return Task.CompletedTask; },
            pollInterval: PollInterval,
            wakeBudget: WakeBudget,
            timeProvider: new NoWaitClock());
    }

    /// <summary>
    /// A clock that reports time as having passed without spending any. Timers complete as soon as they are
    /// created and the clock jumps forward by exactly the delay that was asked for, so a loop written against
    /// a real deadline still stops after the right NUMBER of polls while costing no wall-clock time.
    ///
    /// <para>
    /// This is the fix for a flake, and the flake is worth stating because the shape recurs.
    /// <c>Standby_WakesThenReportsAwake</c> scripts three probe answers and needs the third one, so it needs
    /// two polls; with a 1 ms interval and a 50 ms budget it passed easily on a developer machine and failed
    /// on <c>osx-arm64</c>, where <c>Task.Delay(1)</c> does not cost anything like 1 ms. The budget, not the
    /// script, was deciding the outcome — a test about a sequence of answers was answering a question about
    /// how fast the host was. Every test in this class is now decided by its script alone.
    /// </para>
    ///
    /// <para>
    /// Deliberately not <c>FakeTimeProvider</c> from <c>Microsoft.Extensions.TimeProvider.Testing</c>. That
    /// type is built for tests that drive the clock by hand, which needs the advance to be ordered against a
    /// timer the code under test has not registered yet; this needs no ordering at all, and is small enough
    /// that a package is the larger thing to justify.
    /// </para>
    /// </summary>
    private sealed class NoWaitClock : TimeProvider
    {
        // Mutated only from CreateTimer, which EnsureAwakeAsync reaches one poll at a time, so the reads and
        // writes are already ordered by the awaits between them.
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // The delay is charged to the clock at the moment it is requested rather than when it fires, so
            // the budget is consumed by the number of polls the loop makes and nothing else.
            if (dueTime > TimeSpan.Zero)
            {
                _now += dueTime;
            }

            return new ImmediateTimer(callback, state);
        }

        // Queued rather than invoked inline: Task.Delay is still wiring its continuation up when CreateTimer
        // returns, and a callback that fires before that is a race this fake has no reason to introduce.
        private sealed class ImmediateTimer : ITimer
        {
            public ImmediateTimer(TimerCallback callback, object? state)
                => ThreadPool.QueueUserWorkItem(_ => callback(state));

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task AlreadyAwake_SendsNoWake()
    {
        int wakes = 0;
        var c = Build(new bool?[] { true }, () => wakes++);

        WakeOutcome outcome = await c.EnsureAwakeAsync(progress: null, CancellationToken.None);

        Assert.Equal(WakeOutcome.AlreadyAwake, outcome);
        Assert.Equal(0, wakes);
    }

    [Fact]
    public async Task NoReply_IsNotFoundAndSendsNoWake()
    {
        // A console we cannot see must not be woken blind — the address may be wrong, and connect will say so.
        int wakes = 0;
        var c = Build(new bool?[] { null }, () => wakes++);

        WakeOutcome outcome = await c.EnsureAwakeAsync(progress: null, CancellationToken.None);

        Assert.Equal(WakeOutcome.NotFound, outcome);
        Assert.Equal(0, wakes);
    }

    [Fact]
    public async Task Standby_WakesThenReportsAwake()
    {
        // First probe: standby. Then the wake lands and a later poll sees it awake.
        int wakes = 0;
        var c = Build(new bool?[] { false, false, true }, () => wakes++);

        WakeOutcome outcome = await c.EnsureAwakeAsync(progress: null, CancellationToken.None);

        Assert.Equal(WakeOutcome.Woke, outcome);
        Assert.Equal(1, wakes); // one WAKEUP, not one per poll
    }

    [Fact]
    public async Task Standby_ThatNeverWakes_TimesOut()
    {
        int wakes = 0;
        int probes = 0;
        var c = Build(new bool?[] { false },
                      () => wakes++,
                      () => probes++);

        WakeOutcome outcome = await c.EnsureAwakeAsync(progress: null, CancellationToken.None);

        Assert.Equal(WakeOutcome.TimedOut, outcome);
        Assert.Equal(1, wakes);

        // THE POLL COUNT IS EXACT, and this assertion is the one that keeps this class from flaking again.
        // The budget buys a fixed number of polls - one opening probe, then budget/interval of them - and
        // that is now true on every host, because the clock only advances when the loop asks it to. Under
        // the real clock this number was whatever the machine managed in 50 ms of wall time: 50 on a
        // developer box, far fewer on a loaded CI runner, and the difference is exactly what made
        // Standby_WakesThenReportsAwake report TimedOut on osx-arm64. If somebody removes the injected
        // clock, this fails immediately and says why, rather than going quiet until the next slow host.
        Assert.Equal(1 + (int)(WakeBudget / PollInterval), probes);
    }

    [Fact]
    public async Task Standby_ReportsProgressWhileWaking()
    {
        var reports = new List<string>();
        var progress = new Progress<string>(reports.Add);
        var c = Build(new bool?[] { false, true }, () => { });

        // Progress<T> posts callbacks to the captured context; without a sync context they still run, but on
        // the thread pool, so give them a beat to arrive before asserting.
        await c.EnsureAwakeAsync(progress, CancellationToken.None);
        await Task.Delay(50);

        Assert.Contains(reports, r => r.Contains("Waking", StringComparison.OrdinalIgnoreCase));
    }
}
