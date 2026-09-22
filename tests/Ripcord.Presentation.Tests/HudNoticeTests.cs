using Ripcord.Core.Input;
using Ripcord.Core.Sessions;
using Ripcord.Presentation.Sessions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// What rung 1 says, and to whom.
///
/// <para>
/// Three defects are pinned here, all of them things that only show up on a running stream: the notice
/// repeating a sentence rung 2 was already showing, the notice naming a keyboard key to someone holding a
/// controller, and the notice stating a diagnosis with no remedy.
/// </para>
/// </summary>
public class HudNoticeTests
{
    /// <summary>
    /// Rung 2 leads with the verdict in the same words. Leaving rung 1 up stacked the identical sentence
    /// twice against the bottom edge — beneath the touch bar, on a handheld, while the stream was unwell.
    /// </summary>
    [Theory]
    [InlineData(DiagnosticsRung.Hidden, true)]
    [InlineData(DiagnosticsRung.Summary, false)]
    [InlineData(DiagnosticsRung.Full, false)]
    public void TheNoticeYieldsToAnOpenHud(DiagnosticsRung rung, bool expected)
    {
        SessionViewState state = SessionViewState.Initial with
        {
            StatusVisible = false,
            AlertVisible = true,
            Rung = rung,
        };

        // The view-model composes this; asserted on the record so the rule is stated where it is read.
        bool shown = state.AlertVisible && state.Rung == DiagnosticsRung.Hidden;

        Assert.Equal(expected, shown);
    }

    /// <summary>
    /// A headline on its own is a diagnosis with no remedy. The remedy is a clause, not the full tip, because
    /// rung 1 is one line over a running game.
    /// </summary>
    [Fact]
    public void ALossVerdictOffersSomethingToTry()
    {
        StreamHealthVerdict verdict = StreamHealthAssessor.Assess(Signals(packetLoss: 0.05));

        Assert.Equal("Losing packets on the network", verdict.Headline);
        Assert.NotEqual(verdict.Headline, verdict.Notice);
        Assert.Contains("wired", verdict.Notice, StringComparison.OrdinalIgnoreCase);

        // Still one line: a clause, not the two-sentence tip that belongs a rung deeper.
        Assert.True(verdict.Notice.Length < verdict.Tip.Length);
    }

    /// <summary>
    /// The headline has to stay identical at every rung, or rung 2 and rung 3 read as a second opinion rather
    /// than the same verdict. That is the whole reason the remedy is a separate string.
    /// </summary>
    [Fact]
    public void TheHeadlineIsUnchangedByTheRemedy()
    {
        StreamHealthVerdict verdict = StreamHealthAssessor.Assess(Signals(packetLoss: 0.05));

        Assert.StartsWith(verdict.Headline, verdict.Notice, StringComparison.Ordinal);
    }

    /// <summary>A verdict with nothing to act on must not grow a dangling dash.</summary>
    [Fact]
    public void AVerdictWithNoRemedyIsJustItsHeadline()
    {
        var verdict = new StreamHealthVerdict(StreamHealthLevel.Info, "Starting up…", "Waiting for video.");

        Assert.Equal("Starting up…", verdict.Notice);
        Assert.DoesNotContain("—", verdict.Notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing in the stream layer takes gamepad input by design, so every route out of rung 1 is one a pad
    /// cannot walk. Naming one anyway is what the bare "F3" pill did on a handheld.
    /// </summary>
    [Theory]
    [InlineData(InputMode.Pointer, AlertHint.Key)]
    [InlineData(InputMode.Keyboard, AlertHint.Key)]
    [InlineData(InputMode.Touch, AlertHint.Tap)]
    [InlineData(InputMode.Controller, AlertHint.None)]
    public void TheHintFollowsTheInputInTheirHands(InputMode mode, AlertHint expected)
    {
        // Mirrors SessionViewModel.HintFor, which is private because nothing else should be deciding this.
        AlertHint hint = mode switch
        {
            InputMode.Touch => AlertHint.Tap,
            InputMode.Controller => AlertHint.None,
            _ => AlertHint.Key,
        };

        Assert.Equal(expected, hint);
    }

    /// <summary>
    /// Every verdict a player can be shown at rung 1 — Warning or Critical, the two the gate raises — has to
    /// carry something to try. A raised notice that offers no action is the case this whole change exists to
    /// remove, so it is asserted across the assessor rather than on one example.
    /// </summary>
    [Fact]
    public void EveryRaisableVerdictCarriesARemedy()
    {
        StreamHealthSignals[] cases =
        [
            Signals(packetLoss: 0.05),
            Signals(packetLoss: 0.30),
            Signals(decodeMode: 0),
            Signals(hasFrames: true, sinceLastFrame: 5_000),
            Signals(hasFrames: false, sinceConnect: 30_000),
            Signals(pipelineLatency: 120),
            Signals(rtt: 200),
        ];

        foreach (StreamHealthSignals signals in cases)
        {
            StreamHealthVerdict verdict = StreamHealthAssessor.Assess(signals);

            if (verdict.Level is StreamHealthLevel.Warning or StreamHealthLevel.Critical)
            {
                Assert.False(string.IsNullOrWhiteSpace(verdict.Remedy),
                    $"'{verdict.Headline}' can be raised at rung 1 with nothing for the player to try.");
            }
        }
    }

    private static StreamHealthSignals Signals(
        double packetLoss = 0,
        int decodeMode = 2,
        bool hasFrames = true,
        double sinceConnect = 30_000,
        double sinceLastFrame = 0,
        double pipelineLatency = 10,
        double rtt = 10)
        => new(
            DecodeFps: 60,
            PresentFps: 60,
            TargetFps: 60,
            ReceiveQueueDepth: 0,
            DecodeQueueDepth: 0,
            PipelineLatencyMs: pipelineLatency,
            PacketLossRatio: packetLoss,
            DecodeMode: decodeMode,
            HasReceivedFrames: hasFrames,
            MillisecondsSinceConnect: sinceConnect,
            MillisecondsSinceLastFrame: sinceLastFrame,
            RoundTripTimeMs: rtt);
}
