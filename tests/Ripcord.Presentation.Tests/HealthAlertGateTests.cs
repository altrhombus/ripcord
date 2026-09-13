using Ripcord.Core.Sessions;
using Ripcord.Presentation.Sessions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The rung-1 notice: whether the stream's health is worth interrupting the player about.
///
/// <para>
/// Every case here is a timing one, and none of them waits on wall-clock time — the gate takes the sample
/// time as a parameter, so a five-second hold is a number in a test rather than a five-second test.
/// </para>
/// </summary>
public class HealthAlertGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 20, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void HealthyStreamNeverRaises()
    {
        var gate = new HealthAlertGate();

        for (int second = 0; second < 60; second++)
        {
            Assert.False(gate.Update(StreamHealthLevel.Healthy, At(second)));
        }
    }

    [Fact]
    public void InfoIsNotAProblem()
    {
        // Info covers "Starting up…" and "hardware decode with a memory copy": temporary by construction, or
        // simply worth knowing. Neither earns an interruption over a running game.
        var gate = new HealthAlertGate();

        for (int second = 0; second < 60; second++)
        {
            Assert.False(gate.Update(StreamHealthLevel.Info, At(second)));
        }
    }

    [Fact]
    public void ATwoSecondWarningBlipDoesNotEarnABanner()
    {
        // The one threshold the design states outright.
        var gate = new HealthAlertGate();

        gate.Update(StreamHealthLevel.Warning, At(0));
        Assert.False(gate.Update(StreamHealthLevel.Warning, At(2)));
        Assert.False(gate.Update(StreamHealthLevel.Healthy, At(2.5)));
        Assert.False(gate.IsRaised);
    }

    [Fact]
    public void ASustainedWarningRaisesOnceItsWindowElapses()
    {
        var gate = new HealthAlertGate();

        gate.Update(StreamHealthLevel.Warning, At(0));
        Assert.False(gate.Update(StreamHealthLevel.Warning, At(4.9)));
        Assert.True(gate.Update(StreamHealthLevel.Warning, At(5.0)));
    }

    [Fact]
    public void CriticalRaisesFasterThanWarning()
    {
        var gate = new HealthAlertGate();

        gate.Update(StreamHealthLevel.Critical, At(0));
        Assert.False(gate.Update(StreamHealthLevel.Critical, At(1.9)));
        Assert.True(gate.Update(StreamHealthLevel.Critical, At(2.0)));

        // Stated as a relationship, not two magic numbers: the assessor has already spent a stall grace
        // before it says "stopped", so any wait here is additive to one already served.
        Assert.True(HealthAlertGate.RaiseAfterCritical < HealthAlertGate.RaiseAfterWarning);
    }

    [Fact]
    public void EscalatingFromWarningToCriticalKeepsTheTimeAlreadyServed()
    {
        var gate = new HealthAlertGate();

        gate.Update(StreamHealthLevel.Warning, At(0));
        gate.Update(StreamHealthLevel.Warning, At(1.5));

        // 2s of continuous trouble, and it is now critical: that is the critical window, met. Restarting the
        // clock on escalation would tell a player later for having watched the stream degrade sooner.
        Assert.True(gate.Update(StreamHealthLevel.Critical, At(2.0)));
    }

    [Fact]
    public void RecoveryClearsTheNoticeOnlyAfterTheStreamStaysWell()
    {
        var gate = new HealthAlertGate();

        gate.Update(StreamHealthLevel.Warning, At(0));
        Assert.True(gate.Update(StreamHealthLevel.Warning, At(5)));

        Assert.True(gate.Update(StreamHealthLevel.Healthy, At(6)));
        Assert.True(gate.Update(StreamHealthLevel.Healthy, At(11.9)));
        Assert.False(gate.Update(StreamHealthLevel.Healthy, At(12.0)));
    }

    [Fact]
    public void AFlappingStreamSettlesShownRatherThanStrobing()
    {
        // The anti-strobe invariant. A stream crossing its threshold every few seconds must not blink a
        // banner on and off over the game; it stays shown, which is true, because the stream is unwell.
        var gate = new HealthAlertGate();

        gate.Update(StreamHealthLevel.Warning, At(0));
        Assert.True(gate.Update(StreamHealthLevel.Warning, At(5)));

        double t = 5;
        for (int cycle = 0; cycle < 10; cycle++)
        {
            // Well for three seconds - less than ClearAfter - then unwell again.
            Assert.True(gate.Update(StreamHealthLevel.Healthy, At(t += 1.5)));
            Assert.True(gate.Update(StreamHealthLevel.Healthy, At(t += 1.5)));
            Assert.True(gate.Update(StreamHealthLevel.Warning, At(t += 1.5)));
        }

        Assert.True(gate.IsRaised);
    }

    [Fact]
    public void ClearingIsSlowerThanEitherRise()
    {
        // The relationship the anti-strobe behaviour rests on, asserted directly so that changing one
        // constant without the other fails here rather than in front of a player.
        Assert.True(HealthAlertGate.ClearAfter > HealthAlertGate.RaiseAfterWarning);
        Assert.True(HealthAlertGate.ClearAfter > HealthAlertGate.RaiseAfterCritical);
    }

    [Fact]
    public void TroubleThatRecoversBeforeRaisingStartsOverNextTime()
    {
        var gate = new HealthAlertGate();

        gate.Update(StreamHealthLevel.Warning, At(0));
        gate.Update(StreamHealthLevel.Warning, At(4));   // nearly there
        gate.Update(StreamHealthLevel.Healthy, At(4.5)); // recovered
        gate.Update(StreamHealthLevel.Warning, At(5));   // trouble again

        // The second run gets its own full window; it does not inherit the first one's near-miss.
        Assert.False(gate.Update(StreamHealthLevel.Warning, At(9.9)));
        Assert.True(gate.Update(StreamHealthLevel.Warning, At(10.0)));
    }

    [Fact]
    public void ResetDropsANoticeSoItCannotCrossIntoTheNextSession()
    {
        var gate = new HealthAlertGate();

        gate.Update(StreamHealthLevel.Critical, At(0));
        Assert.True(gate.Update(StreamHealthLevel.Critical, At(2)));

        gate.Reset();
        Assert.False(gate.IsRaised);

        // And the new session starts its own run rather than resuming the old one's.
        Assert.False(gate.Update(StreamHealthLevel.Warning, At(3)));
        Assert.False(gate.Update(StreamHealthLevel.Warning, At(7.9)));
        Assert.True(gate.Update(StreamHealthLevel.Warning, At(8.0)));
    }

    [Fact]
    public void TheFirstSampleNeverRaisesWhateverItSays()
    {
        // A session whose very first verdict is Critical still gets its window: at zero elapsed time nothing
        // has been sustained, and the alternative is a banner during normal start-up.
        var gate = new HealthAlertGate();

        Assert.False(gate.Update(StreamHealthLevel.Critical, At(0)));
    }
}
