using Ripcord.Presentation.Sessions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The connect dwell rule.
///
/// <para>
/// Every one of these is a case that can only be reached on real hardware otherwise: a warm LAN connect that
/// is over before anyone reads it, a console in standby, a video device that hangs. The gate exists so the
/// first is silent and the other two are not, and that trade is exactly what these assert.
/// </para>
/// </summary>
public class ConnectGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 20, 0, 0, TimeSpan.Zero);

    private static ConnectStage Stage(string headline, ConnectPhase phase = ConnectPhase.Preparing, bool terminal = false)
        => new(headline, "detail", terminal, phase);

    /// <summary>
    /// The case the gate was written for: three phases inside the dwell window, and the player reads none of
    /// them. This is a warm connect on a LAN, which is the common case and the one the composition is trying
    /// to keep calm.
    /// </summary>
    [Fact]
    public void AWarmConnectShowsNothingAtAll()
    {
        var gate = new ConnectGate();

        Assert.Null(gate.Offer(Stage("Preparing video…"), T0));
        Assert.Null(gate.Offer(Stage("Checking credentials…"), T0.AddMilliseconds(120)));
        Assert.Null(gate.Offer(Stage("Connecting to your console…", ConnectPhase.Connecting), T0.AddMilliseconds(300)));

        Assert.Null(gate.Shown);
    }

    /// <summary>
    /// And the converse, which is the whole reason the flow announces a phase before running it: a stage that
    /// hangs must name itself. The flow reports once and then goes quiet, so the promotion has to come from
    /// the clock rather than from a second report.
    /// </summary>
    [Fact]
    public void AStageThatHangsNamesItself()
    {
        var gate = new ConnectGate();

        Assert.Null(gate.Offer(Stage("Preparing video…"), T0));

        ConnectStage? shown = gate.Tick(T0.AddSeconds(4));

        Assert.NotNull(shown);
        Assert.Equal("Preparing video…", shown!.Headline);
    }

    [Fact]
    public void ATerminalStageIsNeverWithheld()
    {
        var gate = new ConnectGate();

        ConnectStage? shown = gate.Offer(Stage("Video setup failed", terminal: true), T0);

        Assert.NotNull(shown);
        Assert.Equal("Video setup failed", shown!.Headline);
    }

    /// <summary>
    /// A wake is the start of a wait the player can feel, and it is announced by a phase change. Withholding
    /// it for the dwell would withhold it at the exact moment it begins mattering.
    /// </summary>
    [Fact]
    public void AnEscalationBypassesTheDwell()
    {
        var gate = new ConnectGate();

        gate.Offer(Stage("Preparing video…"), T0);
        gate.Tick(T0.AddSeconds(1));
        Assert.Equal("Preparing video…", gate.Shown!.Headline);

        ConnectStage? shown = gate.Offer(Stage("Waking your PS5…", ConnectPhase.Waking), T0.AddSeconds(1).AddMilliseconds(10));

        Assert.Equal("Waking your PS5…", shown!.Headline);
    }

    /// <summary>
    /// The wake coordinator reports its own progress lines inside one phase. Re-reporting the same line must
    /// not restart its wait, or a phase that keeps talking never gets promoted.
    /// </summary>
    [Fact]
    public void RepeatingTheSameLineDoesNotRestartTheWait()
    {
        var gate = new ConnectGate();

        gate.Offer(Stage("Waking your PS5…", ConnectPhase.Waking), T0);
        gate.Offer(Stage("Waking your PS5…", ConnectPhase.Waking), T0.AddMilliseconds(300));

        ConnectStage? shown = gate.Offer(Stage("Waking your PS5…", ConnectPhase.Waking), T0.AddMilliseconds(610));

        Assert.NotNull(shown);
        Assert.Equal("Waking your PS5…", shown!.Headline);
    }

    /// <summary>
    /// A new line inside the same phase starts its own wait. Otherwise a sequence of quick lines would
    /// accumulate credit and the last one would appear instantly, which is the strobe again wearing a hat.
    /// </summary>
    [Fact]
    public void ANewLineInTheSamePhaseStartsItsOwnWait()
    {
        var gate = new ConnectGate();

        gate.Offer(Stage("Preparing video…"), T0);
        gate.Offer(Stage("Checking credentials…"), T0.AddMilliseconds(500));

        Assert.Null(gate.Tick(T0.AddMilliseconds(900)));
        Assert.NotNull(gate.Tick(T0.AddMilliseconds(1200)));
        Assert.Equal("Checking credentials…", gate.Shown!.Headline);
    }

    /// <summary>
    /// Once a line is on screen it stays there until another earns the screen. A stage that has not dwelled
    /// long enough must not blank what is already showing — that would be a strobe in the other direction.
    /// </summary>
    [Fact]
    public void AShownLineSurvivesAnUnearnedOne()
    {
        var gate = new ConnectGate();

        gate.Offer(Stage("Preparing video…"), T0);
        gate.Tick(T0.AddSeconds(2));

        ConnectStage? shown = gate.Offer(Stage("Checking credentials…"), T0.AddSeconds(2).AddMilliseconds(10));

        Assert.NotNull(shown);
        Assert.Equal("Preparing video…", shown!.Headline);
    }

    [Fact]
    public void ResetClearsTheLastAttempt()
    {
        var gate = new ConnectGate();

        gate.Offer(Stage("Couldn't connect", terminal: true), T0);
        Assert.NotNull(gate.Shown);

        gate.Reset();

        Assert.Null(gate.Shown);
        Assert.Null(gate.Tick(T0.AddSeconds(30)));
    }

    /// <summary>
    /// The dwell has to clear the warm path with room to spare and still be shorter than the point at which a
    /// person starts wondering. Asserted so that tuning it later is a deliberate act rather than a nudge.
    /// </summary>
    [Fact]
    public void TheDwellSitsBetweenAWarmConnectAndHumanPatience()
    {
        Assert.True(ConnectGate.Dwell >= TimeSpan.FromMilliseconds(400));
        Assert.True(ConnectGate.Dwell <= TimeSpan.FromSeconds(1));
    }
}
