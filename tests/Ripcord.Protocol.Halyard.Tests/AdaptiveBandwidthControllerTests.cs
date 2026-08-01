using Ripcord.Core.Sessions;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The adaptive-quality policy. These lock the behaviours that matter in play: step down fast, come back up
/// only on a sustained clean link, never oscillate, and respect battery/thermal limits. Time is injected so
/// the whole ladder is exercised without a single delay.
/// </summary>
public class AdaptiveBandwidthControllerTests
{
    private static readonly SessionConfig Request = new(1920, 1080, 60, 15_000, VideoCodec.H264, LatencyMode.Balanced);

    private DateTimeOffset _now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private AdaptiveBandwidthController NewController() => new(Request, () => _now);

    private void Advance(TimeSpan by) => _now += by;

    // A comfortably meaningful sample size by default, so existing tests exercise the decision logic rather than
    // the small-sample guard. Tests that care about the guard pass units explicitly.
    private const long PlentyOfUnits = 1_000;

    private static NetworkSample Loss(double ratio, double rttMs = 20, long units = PlentyOfUnits)
        => new(rttMs, ratio, JitterMs: 0, ObservedUnits: units);

    private static NetworkSample Clean(double rttMs = 20, long units = PlentyOfUnits)
        => new(rttMs, 0.0, JitterMs: 0, ObservedUnits: units);

    [Fact]
    public void StartsAtTheRequestedQuality()
    {
        BitrateDecision d = NewController().RecommendBitrate();
        Assert.Equal(1920, d.Width);
        Assert.Equal(1080, d.Height);
        Assert.Equal(60, d.Fps);
        Assert.Equal(15_000, d.BitrateKbps);
    }

    [Fact]
    public void Loss_StepsDownBitrateBeforeResolution()
    {
        var c = NewController();
        Advance(AdaptiveBandwidthController.ChangeCooldown);
        c.ReportNetworkSample(Loss(0.05));

        BitrateDecision d = c.RecommendBitrate();
        // Softer, not smaller: resolution and frame rate are preserved on the first step down.
        Assert.Equal(1080, d.Height);
        Assert.Equal(60, d.Fps);
        Assert.True(d.BitrateKbps < 15_000);
    }

    [Fact]
    public void SustainedLoss_EventuallyReducesResolution_ThenFrameRateLast()
    {
        var c = NewController();
        var heights = new List<int>();
        var fpsValues = new List<int>();

        for (int i = 0; i < 12; i++)
        {
            Advance(AdaptiveBandwidthController.ChangeCooldown);
            c.ReportNetworkSample(Loss(0.20));
            BitrateDecision d = c.RecommendBitrate();
            heights.Add(d.Height);
            fpsValues.Add(d.Fps);
        }

        Assert.Contains(720, heights);
        Assert.Equal(540, heights[^1]);

        // Frame rate is the last thing sacrificed: it must still be full rate when resolution first drops.
        int firstResolutionDrop = heights.FindIndex(h => h < 1080);
        Assert.Equal(60, fpsValues[firstResolutionDrop]);
        Assert.True(fpsValues[^1] < 60, "the bottom rung should finally reduce frame rate");
    }

    [Fact]
    public void Cooldown_PreventsMultipleStepsFromABurstOfBadSamples()
    {
        var c = NewController();
        Advance(AdaptiveBandwidthController.ChangeCooldown);

        c.ReportNetworkSample(Loss(0.5));
        int afterFirst = c.RecommendBitrate().BitrateKbps;

        // Same instant: a congestion burst produces many samples, and each must not cost a rung.
        for (int i = 0; i < 10; i++)
        {
            c.ReportNetworkSample(Loss(0.5));
        }

        Assert.Equal(afterFirst, c.RecommendBitrate().BitrateKbps);
    }

    [Fact]
    public void CleanLink_MustBeSustainedBeforeSteppingBackUp()
    {
        var c = NewController();
        Advance(AdaptiveBandwidthController.ChangeCooldown);
        c.ReportNetworkSample(Loss(0.05));
        int degraded = c.RecommendBitrate().BitrateKbps;

        // Clean, but not for long enough yet: past the cooldown, short of the clean streak.
        Advance(AdaptiveBandwidthController.ChangeCooldown);
        c.ReportNetworkSample(Clean());
        Assert.Equal(degraded, c.RecommendBitrate().BitrateKbps);

        // Now sustained.
        Advance(AdaptiveBandwidthController.CleanStreakForStepUp);
        c.ReportNetworkSample(Clean());
        Assert.True(c.RecommendBitrate().BitrateKbps > degraded);
    }

    [Fact]
    public void ASingleBlipDuringRecovery_RestartsTheCleanStreak()
    {
        // This is the anti-oscillation property: a marginal link that is clean-then-bad-then-clean must not
        // ratchet back up, because stepping up into it produces visible repeated glitching.
        var c = NewController();
        Advance(AdaptiveBandwidthController.ChangeCooldown);
        c.ReportNetworkSample(Loss(0.05));
        int degraded = c.RecommendBitrate().BitrateKbps;

        Advance(AdaptiveBandwidthController.CleanStreakForStepUp - TimeSpan.FromSeconds(1));
        c.ReportNetworkSample(Clean());

        // A blip in the hysteresis dead-band: above the step-up threshold (so the link is not "clean") but
        // below the step-down threshold (so it does not warrant losing a rung). It must reset the streak only.
        const double deadBandLoss = 0.01;
        Assert.InRange(
            deadBandLoss,
            AdaptiveBandwidthController.StepUpLossRatio,
            AdaptiveBandwidthController.StepDownLossRatio);
        c.ReportNetworkSample(Loss(deadBandLoss));

        Advance(TimeSpan.FromSeconds(2));    // would have been enough without the blip
        c.ReportNetworkSample(Clean());
        Assert.Equal(degraded, c.RecommendBitrate().BitrateKbps);

        // And the streak still works from the blip onward, so recovery is deferred, not disabled.
        Advance(AdaptiveBandwidthController.CleanStreakForStepUp);
        c.ReportNetworkSample(Clean());
        Assert.True(c.RecommendBitrate().BitrateKbps > degraded);
    }

    [Fact]
    public void HighRtt_SuppressesStepUpEvenWithNoLoss()
    {
        var c = NewController();
        Advance(AdaptiveBandwidthController.ChangeCooldown);
        c.ReportNetworkSample(Loss(0.05));
        int degraded = c.RecommendBitrate().BitrateKbps;

        // Zero loss but a queueing link: raising the rate would only deepen the queue.
        Advance(AdaptiveBandwidthController.CleanStreakForStepUp * 2);
        c.ReportNetworkSample(Clean(rttMs: AdaptiveBandwidthController.StepUpMaxRttMs + 50));
        Assert.Equal(degraded, c.RecommendBitrate().BitrateKbps);
    }

    // ---- small-sample guard ----

    [Fact]
    public void ATinySampleDoesNotStepDownHoweverBadItLooks()
    {
        // The observed regression: the first congestion window after connect held about eight A/V units, so one
        // lost unit read as 12% loss and immediately cost a healthy 1080p60 stream a rung. The overlay showed
        // "stepped down after 12.0% packet loss" beside a 30-second history reading 0.0% — the ratio was real
        // arithmetic on a meaningless sample.
        var c = NewController();

        // Past the cooldown first: _lastChange starts at construction, so without this the controller refuses to
        // change for an unrelated reason and the test would pass without exercising the guard at all.
        Advance(AdaptiveBandwidthController.ChangeCooldown);
        c.ReportNetworkSample(Loss(0.125, units: 8));

        Assert.Equal(1080, c.RecommendBitrate().Height);
        Assert.Equal(15_000, c.RecommendBitrate().BitrateKbps);
    }

    [Fact]
    public void ASingleLostUnitCanNeverOnItsOwnTriggerAStepDown()
    {
        // The threshold is set above 1/StepDownLossRatio for exactly this reason. Checked at the boundary so a
        // future change to either constant that breaks the relationship fails here.
        var c = NewController();
        long units = AdaptiveBandwidthController.MinimumUnitsForLossDecision;

        Advance(AdaptiveBandwidthController.ChangeCooldown);
        c.ReportNetworkSample(Loss(1.0 / units, units: units));

        Assert.Equal(15_000, c.RecommendBitrate().BitrateKbps);
        Assert.True(1.0 / units < AdaptiveBandwidthController.StepDownLossRatio);
    }

    [Fact]
    public void ATinySampleDoesNotDelayARecovery()
    {
        // An uninformative window must not restart the clean streak either, or a trickle of tiny samples would
        // hold quality down indefinitely.
        var c = NewController();

        Advance(AdaptiveBandwidthController.ChangeCooldown);
        c.ReportNetworkSample(Loss(0.20));                       // real loss: step down
        int stepped = c.RecommendBitrate().BitrateKbps;

        c.ReportNetworkSample(Clean());                          // start a clean streak
        Advance(TimeSpan.FromSeconds(6));
        c.ReportNetworkSample(Loss(0.9, units: 4));              // meaningless: must be ignored entirely
        Advance(AdaptiveBandwidthController.CleanStreakForStepUp);
        c.ReportNetworkSample(Clean());

        Assert.True(
            c.RecommendBitrate().BitrateKbps > stepped,
            "a meaningless sample must not reset the clean streak and block recovery");
    }

    [Fact]
    public void RealLossAtAMeaningfulSampleSizeStillStepsDown()
    {
        // The guard must not have disabled the feature it protects.
        var c = NewController();

        Advance(AdaptiveBandwidthController.ChangeCooldown);
        c.ReportNetworkSample(Loss(0.20));

        Assert.True(c.RecommendBitrate().BitrateKbps < 15_000);
    }

    [Fact]
    public void LowBattery_CapsResolution()
    {
        var c = NewController();
        Assert.Equal(1080, c.RecommendBitrate().Height);

        c.ReportThermalPowerState(new PowerState(PowerSource.Battery, 20, ThermalThrottling: false));
        Assert.True(c.RecommendBitrate().Height <= AdaptiveBandwidthController.BatteryMaxHeight);
    }

    [Fact]
    public void HealthyBattery_DoesNotCapAnything()
    {
        // The regression this guards: the cap used to key off "on battery" alone, so a laptop at 94% charge on a
        // flawless link was silently dropped to 720p despite an explicit 1080p request. Being unplugged is not
        // the same as being low.
        var c = NewController();

        c.ReportThermalPowerState(new PowerState(PowerSource.Battery, 94, ThermalThrottling: false));

        Assert.Equal(1080, c.RecommendBitrate().Height);
        Assert.Equal(15_000, c.RecommendBitrate().BitrateKbps);
    }

    [Fact]
    public void BatteryOfUnknownCharge_DoesNotCapOnAGuess()
    {
        var c = NewController();

        c.ReportThermalPowerState(new PowerState(PowerSource.Battery, null, ThermalThrottling: false));

        Assert.Equal(1080, c.RecommendBitrate().Height);
    }

    [Fact]
    public void EnergySaverCapsEvenAtFullCharge()
    {
        // Energy saver is explicit user intent, so unlike a mere battery reading it applies at any charge.
        var c = NewController();

        c.ReportThermalPowerState(
            new PowerState(PowerSource.Battery, 94, ThermalThrottling: false, EnergySaverActive: true));

        Assert.True(c.RecommendBitrate().Height <= AdaptiveBandwidthController.BatteryMaxHeight);
    }

    [Fact]
    public void ThermalThrottling_CapsHarderThanBatteryAlone()
    {
        // Both at a low charge, so the only difference under test is the thermal flag rather than whether the
        // battery-percentage gate happened to engage.
        var battery = NewController();
        battery.ReportThermalPowerState(new PowerState(PowerSource.Battery, 20, ThermalThrottling: false));

        var throttled = NewController();
        throttled.ReportThermalPowerState(new PowerState(PowerSource.Battery, 20, ThermalThrottling: true));

        Assert.True(throttled.RecommendBitrate().BitrateKbps < battery.RecommendBitrate().BitrateKbps);
    }

    [Fact]
    public void ReturningToExternalPower_RestoresEarnedQualityWithoutReclimbing()
    {
        // The device cap is a floor applied over the network-chosen rung, not a move of it — so unplugging and
        // replugging must not cost the user a 12-second climb back per rung.
        var c = NewController();
        c.ReportThermalPowerState(new PowerState(PowerSource.Battery, 30, ThermalThrottling: true));
        Assert.True(c.RecommendBitrate().Height <= AdaptiveBandwidthController.BatteryMaxHeight);

        c.ReportThermalPowerState(new PowerState(PowerSource.ExternalPower, null, ThermalThrottling: false));
        Assert.Equal(1080, c.RecommendBitrate().Height);
        Assert.Equal(15_000, c.RecommendBitrate().BitrateKbps);
    }

    [Fact]
    public void NeverStepsBelowTheBottomRung()
    {
        var c = NewController();
        for (int i = 0; i < 50; i++)
        {
            Advance(AdaptiveBandwidthController.ChangeCooldown);
            c.ReportNetworkSample(Loss(0.9));
        }

        BitrateDecision d = c.RecommendBitrate();
        Assert.True(d.BitrateKbps > 0);
        Assert.True(d.Width > 0 && d.Height > 0 && d.Fps > 0);
    }
}
