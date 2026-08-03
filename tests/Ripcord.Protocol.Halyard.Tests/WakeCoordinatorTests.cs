using Ripcord.Protocol.Halyard.Common.Discovery;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The wake decision logic, driven by fake I/O so no socket is opened. Intervals are tiny so the timed path
/// resolves in milliseconds.
/// </summary>
public class WakeCoordinatorTests
{
    private static HalyardWakeCoordinator Build(Queue<bool?> probeResults, Action onWake)
        => new(
            probeAwake: _ => Task.FromResult(probeResults.Count > 0 ? probeResults.Dequeue() : (bool?)true),
            sendWake: _ => { onWake(); return Task.CompletedTask; },
            pollInterval: TimeSpan.FromMilliseconds(1),
            wakeBudget: TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task AlreadyAwake_SendsNoWake()
    {
        int wakes = 0;
        var c = Build(new Queue<bool?>([true]), () => wakes++);

        WakeOutcome outcome = await c.EnsureAwakeAsync(progress: null, CancellationToken.None);

        Assert.Equal(WakeOutcome.AlreadyAwake, outcome);
        Assert.Equal(0, wakes);
    }

    [Fact]
    public async Task NoReply_IsNotFoundAndSendsNoWake()
    {
        // A console we cannot see must not be woken blind — the address may be wrong, and connect will say so.
        int wakes = 0;
        var c = Build(new Queue<bool?>([null]), () => wakes++);

        WakeOutcome outcome = await c.EnsureAwakeAsync(progress: null, CancellationToken.None);

        Assert.Equal(WakeOutcome.NotFound, outcome);
        Assert.Equal(0, wakes);
    }

    [Fact]
    public async Task Standby_WakesThenReportsAwake()
    {
        // First probe: standby. Then the wake lands and a later poll sees it awake.
        int wakes = 0;
        var c = Build(new Queue<bool?>([false, false, true]), () => wakes++);

        WakeOutcome outcome = await c.EnsureAwakeAsync(progress: null, CancellationToken.None);

        Assert.Equal(WakeOutcome.Woke, outcome);
        Assert.Equal(1, wakes); // one WAKEUP, not one per poll
    }

    [Fact]
    public async Task Standby_ThatNeverWakes_TimesOut()
    {
        int wakes = 0;
        var c = Build(new Queue<bool?>([false, false, false, false, false, false, false, false, false, false]),
                      () => wakes++);

        WakeOutcome outcome = await c.EnsureAwakeAsync(progress: null, CancellationToken.None);

        Assert.Equal(WakeOutcome.TimedOut, outcome);
        Assert.Equal(1, wakes);
    }

    [Fact]
    public async Task Standby_ReportsProgressWhileWaking()
    {
        var reports = new List<string>();
        var progress = new Progress<string>(reports.Add);
        var c = Build(new Queue<bool?>([false, true]), () => { });

        // Progress<T> posts callbacks to the captured context; without a sync context they still run, but on
        // the thread pool, so give them a beat to arrive before asserting.
        await c.EnsureAwakeAsync(progress, CancellationToken.None);
        await Task.Delay(50);

        Assert.Contains(reports, r => r.Contains("Waking", StringComparison.OrdinalIgnoreCase));
    }
}
