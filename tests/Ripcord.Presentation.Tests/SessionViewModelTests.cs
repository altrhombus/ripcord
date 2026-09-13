using Ripcord.Client;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Sessions;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The streaming surface's wording and arithmetic.
///
/// <para>
/// Every case here was previously observable only by connecting to a real console with a real GPU and watching
/// the overlay — which is to say, not observable at all in any repeatable sense. Several of them encode faults
/// that were found that way and fixed blind.
/// </para>
/// </summary>
public class SessionViewModelTests
{
    private static readonly RipcordSettings Defaults = new()
    {
        Width = 1920,
        Height = 1080,
        TargetFps = 60,
        BitrateKbps = 30_000,
        AdaptiveQuality = true,
        ReportConnectionQuality = true,
    };

    /// <summary>A pipeline stub whose clock the test drives, so sampling intervals are exact.</summary>
    private sealed class FakePipeline : IVideoPipelineStats
    {
        public bool IsReady { get; set; } = true;

        public string AdapterDescription { get; set; } = "Test GPU";

        public VideoPipelineSnapshot Snapshot { get; set; }

        public VideoPipelineSnapshot Read() => Snapshot;
    }

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => Now += by;
    }

    private static (SessionViewModel Vm, FakePipeline Pipeline, TestClock Clock) Build(
        RipcordSettings? settings = null)
    {
        var pipeline = new FakePipeline();
        var clock = new TestClock();
        var vm = new SessionViewModel(
            pipeline, settings ?? Defaults, new ImmediateUiDispatcher(), () => clock.Now);

        return (vm, pipeline, clock);
    }

    private static SessionTelemetry Live(
        int bitrateKbps = 20_000,
        double lossRatio = 0,
        double rttMs = 10,
        BitrateDecision? recommended = null,
        string reason = "") =>
        new(
            HasSession: true,
            Statistics: new SessionStatistics(rttMs, bitrateKbps, 60, lossRatio, 5),
            RecommendedQuality: recommended,
            QualityReason: reason,
            MillisecondsSinceConnect: 5_000,
            MillisecondsSinceLastFrame: 16);

    // ---- status overlay ------------------------------------------------------------------------

    [Fact]
    public void ShowStatus_NonTerminal_StaysBusyAndOffersNothing()
    {
        (SessionViewModel vm, _, _) = Build();

        vm.ShowStatus("Connecting…", "Reaching the console.", terminal: false);

        Assert.True(vm.State.StatusVisible);
        Assert.True(vm.State.StatusBusy);
        Assert.False(vm.State.StatusActionsVisible);
    }

    [Fact]
    public void ShowStatus_Terminal_StopsBusyAndOffersActions()
    {
        // Terminal means the session will not leave this state on its own, so a spinner would be a lie and the
        // user needs something to press.
        (SessionViewModel vm, _, _) = Build();

        vm.ShowStatus("Video setup failed", "No device.", terminal: true);

        Assert.False(vm.State.StatusBusy);
        Assert.True(vm.State.StatusActionsVisible);
    }

    [Fact]
    public void ApplyLifecycle_Streaming_HidesTheOverlayAndMarksTheStreamLive()
    {
        (SessionViewModel vm, _, _) = Build();
        vm.ShowStatus("Connecting…", string.Empty, terminal: false);

        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));

        Assert.False(vm.State.StatusVisible);
        Assert.True(vm.State.IsStreamLive);
    }

    [Fact]
    public void ApplyLifecycle_Degraded_ReportsQuietlyAndKeepsTheStreamLive()
    {
        // Most stalls recover in under a second. Flashing a frightening overlay over a picture that is about to
        // come back — and dropping out of immersive mode with it — is worse than saying nothing loudly.
        (SessionViewModel vm, _, _) = Build();
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));

        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Degraded, "Frames stopped"));

        Assert.True(vm.State.IsStreamLive);
        Assert.Equal("Reconnecting the video…", vm.State.StatusHeadline);
        Assert.True(vm.State.StatusBusy);
    }

    [Fact]
    public void ApplyLifecycle_Failed_IsTerminalAndEndsTheStream()
    {
        (SessionViewModel vm, _, _) = Build();
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));

        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Failed, "Console refused the session"));

        Assert.False(vm.State.IsStreamLive);
        Assert.True(vm.State.StatusActionsVisible);
        Assert.Equal("Console refused the session", vm.State.StatusDetail);
    }

    // ---- sampling ------------------------------------------------------------------------------

    [Fact]
    public void Sample_WithNoPipeline_IsSkipped()
    {
        (SessionViewModel vm, FakePipeline pipeline, _) = Build();
        pipeline.IsReady = false;

        Assert.False(vm.Sample(Live()));
    }

    [Fact]
    public void Sample_TwiceInQuickSuccession_SkipsTheSecond()
    {
        // The bug this pins: a DispatcherTimer that has been delayed fires again almost immediately afterwards,
        // and dividing a burst of frames by a ~37 ms gap reported 1610 fps — impossible, and it poisoned the
        // 30-second PEAK permanently, because a maximum never decays.
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();

        pipeline.Snapshot = new VideoPipelineSnapshot(0, 0, 0, 0, 2);
        vm.Sample(Live());

        clock.Advance(TimeSpan.FromMilliseconds(500));
        pipeline.Snapshot = new VideoPipelineSnapshot(30, 30, 0, 0, 2);
        Assert.True(vm.Sample(Live()));
        Assert.Equal("60", vm.State.Diagnostics.HeroFps);

        // 37 ms later, 60 more frames. Taken at face value that is 1621 fps.
        clock.Advance(TimeSpan.FromMilliseconds(37));
        pipeline.Snapshot = new VideoPipelineSnapshot(90, 90, 0, 0, 2);

        Assert.False(vm.Sample(Live()));
        Assert.Equal("60", vm.State.Diagnostics.HeroFps);
    }

    [Fact]
    public void Sample_AfterASkippedInterval_MeasuresFromTheLastGoodSample()
    {
        // Skipping rather than clamping is the point: the frames are still counted, just over an honest window.
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();

        pipeline.Snapshot = new VideoPipelineSnapshot(0, 0, 0, 0, 2);
        vm.Sample(Live());

        clock.Advance(TimeSpan.FromMilliseconds(37));
        pipeline.Snapshot = new VideoPipelineSnapshot(60, 60, 0, 0, 2);
        vm.Sample(Live());

        // One full second after the LAST GOOD sample, 60 frames total: 60 fps, not 1621 and not 62.
        clock.Advance(TimeSpan.FromMilliseconds(963));
        pipeline.Snapshot = new VideoPipelineSnapshot(60, 60, 0, 0, 2);
        vm.Sample(Live());

        Assert.Equal("60", vm.State.Diagnostics.HeroFps);
    }

    // ---- resolution wording --------------------------------------------------------------------

    [Fact]
    public void Resolution_BeforeAnyFrame_IsPending()
    {
        (SessionViewModel vm, FakePipeline pipeline, _) = Build();
        pipeline.Snapshot = new VideoPipelineSnapshot(0, 0, 0, 0, 2);

        vm.Sample(Live());

        Assert.Equal("—", vm.State.Diagnostics.HeroResolution);
    }

    [Fact]
    public void Decoder_ReportingFramesButNoGeometry_SaysSoRatherThanWaiting()
    {
        // These are different faults with the same symptom. Frames decoding with no reported size renders a
        // BLACK SCREEN while every other counter looks healthy, so it must not hide behind a word that means
        // "wait a moment".
        (SessionViewModel vm, FakePipeline pipeline, _) = Build();
        pipeline.Snapshot = new VideoPipelineSnapshot(
            DecodedFrames: 120, PresentedFrames: 120, QueueDepth: 0, PipelineLatencyMs: 8, DecodeMode: 2,
            DecodedWidth: 0, DecodedHeight: 0);

        vm.Sample(Live());

        Assert.True(vm.State.Diagnostics.ReasonVisible);
        Assert.Contains("decoder reported no frame size", vm.State.Diagnostics.Reason);
    }

    [Fact]
    public void Reason_WhenTheConsoleChoseASmallerPicture_SaysWhyAndWhatToDo()
    {
        (SessionViewModel vm, FakePipeline pipeline, _) = Build();
        pipeline.Snapshot = new VideoPipelineSnapshot(
            DecodedFrames: 120, PresentedFrames: 120, QueueDepth: 0, PipelineLatencyMs: 8, DecodeMode: 2,
            DecodedWidth: 1280, DecodedHeight: 720);

        vm.Sample(Live());

        Assert.Contains("console chose 1280×720", vm.State.Diagnostics.Reason);
        Assert.Contains("raise it for more", vm.State.Diagnostics.Reason);
    }

    // ---- capability pills ----------------------------------------------------------------------

    [Fact]
    public void Pills_HdrOutput_IsTheOnlyThingThatLightsTheHdrPill()
    {
        // The fault this pins: a previous version substring-matched the decoder description for "PQ", which is
        // present whenever the STREAM is PQ — so the HDR pill lit while the driver was tone-mapping to SDR on an
        // SDR display, claiming a capability that was not in effect.
        (SessionViewModel vm, FakePipeline pipeline, _) = Build();
        pipeline.Snapshot = new VideoPipelineSnapshot(
            DecodedFrames: 60, PresentedFrames: 60, QueueDepth: 0, PipelineLatencyMs: 8, DecodeMode: 2,
            Decoder: "HEVC (PQ transfer)", IsHdrOutput: false, IsTenBit: true);

        vm.Sample(Live());

        Assert.DoesNotContain(vm.State.Diagnostics.CapabilityPills, p => p.Label == "HDR");
        Assert.Contains(vm.State.Diagnostics.CapabilityPills, p => p.Label == "10-bit");
    }

    [Fact]
    public void Pills_AreTheSameInstanceWhileTheSetIsUnchanged()
    {
        // Not a micro-optimisation: a fresh list every tick makes record equality fail every tick, so the state
        // would report a change — and every binding would re-evaluate — twice a second, forever.
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();
        var snapshot = new VideoPipelineSnapshot(
            DecodedFrames: 60, PresentedFrames: 60, QueueDepth: 0, PipelineLatencyMs: 8, DecodeMode: 2,
            Decoder: "HEVC", IsHdrOutput: true);

        pipeline.Snapshot = snapshot;
        vm.Sample(Live());
        IReadOnlyList<CapabilityPill> first = vm.State.Diagnostics.CapabilityPills;

        clock.Advance(TimeSpan.FromMilliseconds(500));
        pipeline.Snapshot = snapshot with { DecodedFrames = 90, PresentedFrames = 90 };
        vm.Sample(Live());

        Assert.Same(first, vm.State.Diagnostics.CapabilityPills);
    }

    // ---- adaptive quality ----------------------------------------------------------------------

    [Fact]
    public void Adaptive_WhenTheTargetMatchesTheRequest_SaysNothing()
    {
        // "adaptive: exactly what you configured", twice a second, is noise.
        (SessionViewModel vm, FakePipeline pipeline, _) = Build();
        pipeline.Snapshot = new VideoPipelineSnapshot(60, 60, 0, 8, 2, DecodedWidth: 1920, DecodedHeight: 1080);

        vm.Sample(Live(recommended: new BitrateDecision(30_000, 1920, 1080, 60)));

        Assert.False(vm.State.Diagnostics.AdaptiveVisible);
    }

    [Fact]
    public void Adaptive_WithReportingOff_SaysTheTargetIsNotBeingSent()
    {
        // The distinction matters: a target the console never hears about explains why nothing changed.
        RipcordSettings noReporting = Defaults with { ReportConnectionQuality = false };
        (SessionViewModel vm, FakePipeline pipeline, _) = Build(noReporting);
        pipeline.Snapshot = new VideoPipelineSnapshot(60, 60, 0, 8, 2, DecodedWidth: 1920, DecodedHeight: 1080);

        vm.Sample(Live(recommended: new BitrateDecision(12_000, 1920, 1080, 60)));

        Assert.True(vm.State.Diagnostics.AdaptiveVisible);
        Assert.Contains("not sent (reporting off)", vm.State.Diagnostics.Adaptive);
    }

    [Fact]
    public void Adaptive_PreferringADifferentResolution_SaysItNeedsAReconnect()
    {
        // CONNECTION_QUALITY carries a bitrate only — resolution is fixed by the launchSpec at session start —
        // so a preferred resolution must never be implied to be in effect.
        (SessionViewModel vm, FakePipeline pipeline, _) = Build();
        pipeline.Snapshot = new VideoPipelineSnapshot(60, 60, 0, 8, 2, DecodedWidth: 1920, DecodedHeight: 1080);

        vm.Sample(Live(recommended: new BitrateDecision(12_000, 1280, 720, 60)));

        Assert.Contains("needs a reconnect", vm.State.Diagnostics.Adaptive);
    }

    // ---- headroom and health -------------------------------------------------------------------

    [Fact]
    public void Headroom_IsClampedToTheCap()
    {
        // A stream briefly exceeding its own cap must not draw a bar wider than the row.
        (SessionViewModel vm, FakePipeline pipeline, _) = Build();
        pipeline.Snapshot = new VideoPipelineSnapshot(60, 60, 0, 8, 2, DecodedWidth: 1920, DecodedHeight: 1080);

        vm.Sample(Live(bitrateKbps: 45_000));

        Assert.Equal(1.0, vm.State.Diagnostics.HeadroomUsedFraction);
    }

    [Fact]
    public void Health_WithNoSession_DoesNotJudgeAStreamThatHasNotStarted()
    {
        // The assessor's inputs are all zero without a session, and "frame rate is below target" is a misleading
        // thing to say about a stream that has not started.
        (SessionViewModel vm, FakePipeline pipeline, _) = Build();
        pipeline.Snapshot = new VideoPipelineSnapshot(0, 0, 0, 0, 0);

        vm.Sample(SessionTelemetry.None);

        Assert.Equal("Not connected yet", vm.State.Diagnostics.Health);
    }

    [Fact]
    public void Histories_AreNotRecordedWithoutASession()
    {
        // Otherwise the 30-second peaks open every session already poisoned with zeroes.
        (SessionViewModel vm, FakePipeline pipeline, _) = Build();
        pipeline.Snapshot = new VideoPipelineSnapshot(0, 0, 0, 0, 0);

        vm.Sample(SessionTelemetry.None);

        Assert.Equal(0, vm.FpsHistory.Count);
    }

    // ---- attached controllers ------------------------------------------------------------------

    [Fact]
    public void Controllers_WithNoneAttached_SaysSo()
    {
        (SessionViewModel vm, _, _) = Build();

        Assert.Equal("none attached", vm.State.ConnectedControllers);
    }

    [Fact]
    public void Controllers_ReportEngineTransportAndId()
    {
        // All three, because the connection event carries all three and they used to be discarded — which made a
        // hardware test inconclusive.
        (SessionViewModel vm, _, _) = Build();

        vm.ApplyControllerConnection("pad-1", connected: true, ControllerLink.Bluetooth, "GameInput");

        Assert.Equal("pad-1 · Bluetooth · GameInput", vm.State.ConnectedControllers);
    }

    [Fact]
    public void Controllers_AreTrackedAsASet()
    {
        // Every engine runs at once and several pads can be attached; "last event wins" hid all but one.
        (SessionViewModel vm, _, _) = Build();

        vm.ApplyControllerConnection("pad-1", connected: true, ControllerLink.Usb, "GameInput");
        vm.ApplyControllerConnection("pad-2", connected: true, ControllerLink.Bluetooth, "DualSense");

        Assert.Equal(2, vm.State.ConnectedControllers.Split('\n').Length);

        vm.ApplyControllerConnection("pad-1", connected: false, ControllerLink.Usb, "GameInput");

        Assert.Equal("pad-2 · Bluetooth · DualSense", vm.State.ConnectedControllers);
    }

    // ---- rung 1: the notice over a running game ------------------------------------------------

    /// <summary>
    /// Run a live stream for <paramref name="seconds"/>, sampling every half second the way the real stats
    /// timer does, presenting a healthy 60 fps throughout. Frames have to keep arriving or the verdict is
    /// about the missing picture rather than about the loss, which is what these cases are testing.
    /// </summary>
    private static void Stream(
        SessionViewModel vm, FakePipeline pipeline, TestClock clock, double seconds, double lossRatio)
    {
        long frames = pipeline.Snapshot.PresentedFrames;

        for (double elapsed = 0; elapsed < seconds; elapsed += 0.5)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            frames += 30;
            pipeline.Snapshot = new VideoPipelineSnapshot(frames, frames, 0, 0, 2, 1920, 1080);
            vm.Sample(Live(lossRatio: lossRatio));
        }
    }

    [Fact]
    public void AHealthyStreamStaysSilentHoweverLongItRuns()
    {
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));

        Stream(vm, pipeline, clock, seconds: 60, lossRatio: 0);

        Assert.Equal(StreamHealthLevel.Healthy, vm.State.Diagnostics.HealthLevel);
        Assert.False(vm.State.AlertVisible);
    }

    [Fact]
    public void ABriefWobbleNeverReachesTheScreen()
    {
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));
        Stream(vm, pipeline, clock, seconds: 10, lossRatio: 0);

        // Two seconds of loss - exactly the blip the design refuses to raise a banner for.
        Stream(vm, pipeline, clock, seconds: 2, lossRatio: 0.05);

        Assert.Equal(StreamHealthLevel.Warning, vm.State.Diagnostics.HealthLevel);
        Assert.False(vm.State.AlertVisible);
    }

    [Fact]
    public void SustainedTroubleRaisesTheNotice()
    {
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));

        Stream(vm, pipeline, clock, seconds: 8, lossRatio: 0.05);

        Assert.True(vm.State.AlertVisible);

        // It says what the panel says, rather than inventing a second vocabulary for the same situation.
        Assert.Equal(StreamHealthLevel.Warning, vm.State.Diagnostics.HealthLevel);
        Assert.False(string.IsNullOrWhiteSpace(vm.State.Diagnostics.Health));
    }

    [Fact]
    public void TheNoticeIsSuppressedWhileTheStatusOverlayIsUp()
    {
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));
        Stream(vm, pipeline, clock, seconds: 8, lossRatio: 0.05);
        Assert.True(vm.State.AlertVisible);

        // The overlay is a stronger statement about the same situation. Two notices about one problem read
        // as two problems.
        vm.ShowStatus("Reconnecting…", "Lost the console", terminal: false);

        Assert.True(vm.State.StatusVisible);
        Assert.False(vm.State.AlertVisible);
    }

    [Fact]
    public void AReconnectStartsWithNothingSustained()
    {
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));
        Stream(vm, pipeline, clock, seconds: 8, lossRatio: 0.05);
        Assert.True(vm.State.AlertVisible);

        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Reconnecting, "Reconnecting"));
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));
        Assert.False(vm.State.AlertVisible);

        // Reset, not merely hidden: fresh trouble has to earn the notice again from zero.
        Stream(vm, pipeline, clock, seconds: 2, lossRatio: 0.05);
        Assert.False(vm.State.AlertVisible);
    }

    // ---- instrument severities: a coloured stroke has to mean something ------------------------

    [Fact]
    public void AHealthyStreamPlotsEveryLineNeutral()
    {
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));
        Stream(vm, pipeline, clock, seconds: 4, lossRatio: 0);

        // The bug this replaces: loss drew red at zero loss and frames drew green while collapsing, so no
        // stroke colour on the panel carried any information at all.
        Assert.Equal(MetricSeverity.Normal, vm.State.Diagnostics.LossSeverity);
        Assert.Equal(MetricSeverity.Normal, vm.State.Diagnostics.LatencySeverity);
        Assert.Equal(MetricSeverity.Normal, vm.State.Diagnostics.FramesSeverity);
    }

    [Theory]
    [InlineData(0.0, MetricSeverity.Normal)]
    [InlineData(0.019, MetricSeverity.Normal)]
    [InlineData(0.02, MetricSeverity.Warning)]
    [InlineData(0.05, MetricSeverity.Warning)]
    [InlineData(0.10, MetricSeverity.Critical)]
    [InlineData(0.40, MetricSeverity.Critical)]
    public void LossWearsTheAssessorsOwnThresholds(double lossRatio, MetricSeverity expected)
    {
        // Deliberately the assessor's constants rather than numbers restated here: the row and the verdict
        // have to agree, and two copies of a threshold is how they stop agreeing.
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));
        Stream(vm, pipeline, clock, seconds: 2, lossRatio: lossRatio);

        Assert.Equal(expected, vm.State.Diagnostics.LossSeverity);
    }

    [Theory]
    [InlineData(10, MetricSeverity.Normal)]
    [InlineData(59, MetricSeverity.Normal)]
    [InlineData(60, MetricSeverity.Warning)]
    [InlineData(119, MetricSeverity.Warning)]
    [InlineData(120, MetricSeverity.Critical)]
    public void LatencyWearsTheAssessorsOwnThresholds(double rttMs, MetricSeverity expected)
    {
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        pipeline.Snapshot = new VideoPipelineSnapshot(30, 30, 0, 0, 2, 1920, 1080);
        vm.Sample(Live(rttMs: rttMs));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        pipeline.Snapshot = new VideoPipelineSnapshot(60, 60, 0, 0, 2, 1920, 1080);
        vm.Sample(Live(rttMs: rttMs));

        Assert.Equal(expected, vm.State.Diagnostics.LatencySeverity);
    }

    [Fact]
    public void FramesGoWarningBelowTheShortfallFactor()
    {
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();
        vm.ApplyLifecycle(new SessionStatus(SessionLifecycle.Streaming, "Streaming"));

        // 20 frames per half second is 40 fps against a 60 fps target - below 0.75x, so a shortfall.
        long frames = 0;
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            frames += 20;
            pipeline.Snapshot = new VideoPipelineSnapshot(frames, frames, 0, 0, 2, 1920, 1080);
            vm.Sample(Live());
        }

        Assert.Equal(MetricSeverity.Warning, vm.State.Diagnostics.FramesSeverity);
    }

    [Fact]
    public void ThePlotsDeclareWhereTheirThresholdIsDrawn()
    {
        (SessionViewModel vm, _, _) = Build();

        // Loss and latency are scaled so full scale IS the warn threshold, so the rule sits along the
        // ceiling and "touching the top" reads as "this is now a problem".
        Assert.Equal(1, SessionViewModel.LossPlot.WarnFraction);
        Assert.Equal(1, SessionViewModel.LatencyPlot.WarnFraction);

        // Frames is the one plot whose threshold is not its ceiling: it is scaled with headroom so a stream
        // at target is not drawn clipped, which puts the shortfall rule partway down.
        Assert.NotNull(vm.FramesPlot.WarnFraction);
        Assert.InRange(vm.FramesPlot.WarnFraction!.Value, 0.6, 0.65);

        // Bitrate has no threshold to draw, because there is no bitrate that is wrong by itself.
        Assert.Null(vm.BitratePlot.WarnFraction);
    }

    [Fact]
    public void ScalesAreFixedRatherThanDerivedFromTheSamples()
    {
        // An auto-scaled axis makes a calm stream and a broken one look identical, so the scale must not
        // move when the data does.
        (SessionViewModel vm, FakePipeline pipeline, TestClock clock) = Build();
        double before = vm.FramesPlot.FullScale;

        Stream(vm, pipeline, clock, seconds: 6, lossRatio: 0.3);

        Assert.Equal(before, vm.FramesPlot.FullScale);
        Assert.Equal(SessionViewModel.LossPlot.FullScale, SessionViewModel.LossPlot.FullScale);
    }
}
