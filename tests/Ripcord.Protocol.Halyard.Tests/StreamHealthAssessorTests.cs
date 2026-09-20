using Ripcord.Core.Sessions;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The stream-health interpretation: raw telemetry → one plain-language verdict, most-actionable-first. These
/// lock the rule priority so the UI (which just renders the verdict) shows the right advice.
/// </summary>
public class StreamHealthAssessorTests
{
    // A healthy baseline: 60fps, no loss, empty queues, low latency, zero-copy, frames flowing. Individual
    // tests override. HasReceivedFrames must be true here or the liveness rules (which run first) fire instead.
    private static StreamHealthSignals Healthy(
        double loss = 0, int rxq = 0, int decodeq = 0, double latencyMs = 20,
        int decodeMode = 2, double presentFps = 60, double decodeFps = 60, int targetFps = 60,
        double msSinceLastFrame = 16, double msSinceConnect = 30_000, double rttMs = 10)
        => new(decodeFps, presentFps, targetFps, rxq, decodeq, latencyMs, loss, decodeMode,
               HasReceivedFrames: true,
               MillisecondsSinceLastFrame: msSinceLastFrame,
               MillisecondsSinceConnect: msSinceConnect,
               RoundTripTimeMs: rttMs);

    [Fact]
    public void AllNominal_IsHealthy()
    {
        StreamHealthVerdict v = StreamHealthAssessor.Assess(Healthy());
        Assert.Equal(StreamHealthLevel.Healthy, v.Level);
    }

    [Fact]
    public void NoFramesYet_WithinStartupGrace_ReportsStartingUp()
    {
        var v = StreamHealthAssessor.Assess(new StreamHealthSignals(
            0, 0, 60, 0, 0, 0, 0, 2, HasReceivedFrames: false, MillisecondsSinceConnect: 500));
        Assert.Equal(StreamHealthLevel.Info, v.Level);
        Assert.Contains("Starting", v.Headline);
    }

    [Fact]
    public void NoFramesEver_PastStartupGrace_IsCriticalNotStartingUp()
    {
        // The regression this guards: a connect that never produced video used to read "Starting up…" forever.
        var v = StreamHealthAssessor.Assess(new StreamHealthSignals(
            0, 0, 60, 0, 0, 0, 0, 2,
            HasReceivedFrames: false,
            MillisecondsSinceConnect: StreamHealthAssessor.StartupGraceMs + 1));

        Assert.Equal(StreamHealthLevel.Critical, v.Level);
        Assert.DoesNotContain("Starting", v.Headline);
        Assert.Contains("No video", v.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FramesStopped_IsCriticalStall()
    {
        // Frames were flowing and then stopped: fps deltas read as zero, so without the liveness rule this
        // looked identical to a healthy idle stream.
        var v = StreamHealthAssessor.Assess(Healthy(
            presentFps: 0, decodeFps: 0, msSinceLastFrame: StreamHealthAssessor.StallMs + 1));

        Assert.Equal(StreamHealthLevel.Critical, v.Level);
        Assert.Contains("stopped", v.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StallOutranksEverythingElse()
    {
        // A stalled stream with loss and software decode still reports the stall — it is the only actionable fact.
        var v = StreamHealthAssessor.Assess(Healthy(
            loss: 0.5, decodeMode: 0, presentFps: 0, decodeFps: 0,
            msSinceLastFrame: StreamHealthAssessor.StallMs + 1));

        Assert.Contains("stopped", v.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HighRtt_WithOtherwiseCleanStream_IsReported()
    {
        StreamHealthVerdict v = StreamHealthAssessor.Assess(Healthy(rttMs: StreamHealthAssessor.RttBadMs + 10));
        Assert.Equal(StreamHealthLevel.Warning, v.Level);
        Assert.Contains("delay", v.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModerateRtt_RanksBelowFrameRateShortfall()
    {
        // Fidelity problems outrank feel problems: a below-target frame rate is the more useful headline.
        StreamHealthVerdict v = StreamHealthAssessor.Assess(Healthy(
            presentFps: 30, decodeFps: 30, targetFps: 60, rttMs: StreamHealthAssessor.RttWarnMs + 5));
        Assert.Contains("Frame rate", v.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loss_WhilePresentingWhatWeDecode_IsNetwork()
    {
        // Frames arrive, decode and reach the screen; the console still reports loss. Nothing local to blame.
        StreamHealthVerdict v = StreamHealthAssessor.Assess(Healthy(loss: 0.05, presentFps: 58, decodeFps: 60));
        Assert.Equal(StreamHealthLevel.Warning, v.Level);
        Assert.Contains("network", v.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wired", v.Tip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loss_WhileSheddingDecodedFrames_IsDevice()
    {
        // The real shape of the device-starved run, sample for sample: 61.3 fps decoded, 0 presented, loss
        // reported, and a receive queue of exactly 0 - which is why depth cannot be the discriminator.
        StreamHealthVerdict v = StreamHealthAssessor.Assess(
            Healthy(loss: 0.1148, rxq: 0, presentFps: 0.1, decodeFps: 61.3, decodeMode: 1));

        Assert.Equal(StreamHealthLevel.Critical, v.Level);
        Assert.Contains("device", v.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loss_IsNotBlamedOnTheDeviceJustBecauseTheReceiveQueueSpiked()
    {
        // The measured failure: an instantaneous depth sampled at 2 Hz against bursty arrivals spiked to 266
        // on a sample whose neighbours both read 0. That must no longer move the verdict at all, so this run
        // is the network case wearing the old device tell.
        StreamHealthVerdict v = StreamHealthAssessor.Assess(
            Healthy(loss: 0.05, rxq: 266, presentFps: 58, decodeFps: 60));

        Assert.Contains("network", v.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SheddingDecodedFrames_WithoutLoss_OutranksTheReadbackNote()
    {
        // The ordering bug: with a readback decode path and no loss, rule 4 said "This is fine" while the
        // stream presented 4 fps out of 60 decoded. 16-17% of samples in both device-starved runs landed on a
        // Healthy/Info verdict below 45 fps, and this is the one a player checks before giving up.
        StreamHealthVerdict v = StreamHealthAssessor.Assess(
            Healthy(loss: 0, presentFps: 3.9, decodeFps: 58.6, decodeMode: 1, latencyMs: 30));

        Assert.Equal(StreamHealthLevel.Warning, v.Level);
        Assert.DoesNotContain("fine", v.Tip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AStoppedStreamIsNotReportedAsShedding()
    {
        // Zero presented out of zero decoded is a stall, not shedding, and the liveness rule owns it. Without
        // the DecodeFps guard this reads as total frame-dropping and blames the GPU for a dead link.
        StreamHealthVerdict v = StreamHealthAssessor.Assess(
            Healthy(loss: 0, presentFps: 0, decodeFps: 0, decodeMode: 2));

        Assert.DoesNotContain("dropping frames", v.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SevereLoss_IsCritical()
    {
        StreamHealthVerdict v = StreamHealthAssessor.Assess(Healthy(loss: 0.30, rxq: 0));
        Assert.Equal(StreamHealthLevel.Critical, v.Level);
    }

    [Fact]
    public void LossOutranksDecodePathAndLatency()
    {
        // Even with software decode + high latency, active loss is the headline (most actionable).
        StreamHealthVerdict v = StreamHealthAssessor.Assess(Healthy(loss: 0.05, decodeMode: 0, latencyMs: 200));
        Assert.Contains("network", v.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SoftwareDecode_Warns()
    {
        StreamHealthVerdict v = StreamHealthAssessor.Assess(Healthy(decodeMode: 0));
        Assert.Equal(StreamHealthLevel.Warning, v.Level);
        Assert.Contains("CPU", v.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HighLatency_WithEmptyQueues_WarnsSlowDecode()
    {
        StreamHealthVerdict v = StreamHealthAssessor.Assess(Healthy(latencyMs: 120));
        Assert.Equal(StreamHealthLevel.Warning, v.Level);
        Assert.Contains("slowly", v.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadbackDecode_IsInfoNotWarning()
    {
        StreamHealthVerdict v = StreamHealthAssessor.Assess(Healthy(decodeMode: 1));
        Assert.Equal(StreamHealthLevel.Info, v.Level);
    }

    [Fact]
    public void LowFrameRate_NoLoss_IsInfo()
    {
        StreamHealthVerdict v = StreamHealthAssessor.Assess(Healthy(presentFps: 30, decodeFps: 30, targetFps: 60));
        Assert.Equal(StreamHealthLevel.Info, v.Level);
        Assert.Contains("Frame rate", v.Headline, StringComparison.OrdinalIgnoreCase);
    }
}
