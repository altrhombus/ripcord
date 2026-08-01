using Ripcord.Core.Power;
using Ripcord.Core.Sessions;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The power-monitor debounce policy and the quality caps it drives.
///
/// <para>
/// The reading itself needs a real Windows host, so what is tested here is the part that would misbehave
/// silently: which transitions are worth telling the bandwidth controller about (it re-evaluates quality on
/// every report, so a battery percentage ticking down one point must not trigger one), and that each device
/// signal actually reduces quality — previously the caps existed but nothing ever reported battery or energy
/// saver, so they could never engage at all.
/// </para>
/// </summary>
public class PowerThermalMonitorTests
{
    private static PowerState Ac(int? percent = null) =>
        new(PowerSource.ExternalPower, percent, ThermalThrottling: false);

    private static PowerState Battery(
        int percent, bool energySaver = false, bool critical = false, bool thermal = false) =>
        new(PowerSource.Battery, percent, thermal, energySaver, critical);

    // ---- debounce policy ----

    [Fact]
    public void PowerSourceChange_IsMaterial()
    {
        Assert.True(PowerThermalMonitor.IsMaterialChange(Ac(100), Battery(100)));
        Assert.True(PowerThermalMonitor.IsMaterialChange(Battery(100), Ac(100)));
    }

    [Fact]
    public void EnergySaverAndCriticalBatteryChanges_AreMaterial()
    {
        Assert.True(PowerThermalMonitor.IsMaterialChange(Battery(50), Battery(50, energySaver: true)));
        Assert.True(PowerThermalMonitor.IsMaterialChange(Battery(50), Battery(50, critical: true)));
        Assert.True(PowerThermalMonitor.IsMaterialChange(Battery(50), Battery(50, thermal: true)));
    }

    [Fact]
    public void SmallBatteryDrift_IsNotMaterial()
    {
        // The controller re-evaluates quality on every report; 61 -> 60 is not a reason to do that.
        Assert.False(PowerThermalMonitor.IsMaterialChange(Battery(61), Battery(60)));
        Assert.False(PowerThermalMonitor.IsMaterialChange(Battery(69), Battery(65)));
    }

    [Fact]
    public void CrossingATenPercentBoundary_IsMaterial()
    {
        Assert.True(PowerThermalMonitor.IsMaterialChange(Battery(60), Battery(59)));
        Assert.True(PowerThermalMonitor.IsMaterialChange(Battery(30), Battery(19)));
    }

    [Fact]
    public void GainingOrLosingABatteryReading_IsMaterial()
    {
        Assert.True(PowerThermalMonitor.IsMaterialChange(Ac(null), Ac(90)));
        Assert.True(PowerThermalMonitor.IsMaterialChange(Ac(90), Ac(null)));
    }

    [Fact]
    public void IdenticalReadings_AreNotMaterial()
    {
        Assert.False(PowerThermalMonitor.IsMaterialChange(Battery(55), Battery(55)));
        Assert.False(PowerThermalMonitor.IsMaterialChange(Ac(), Ac()));
    }

    // ---- factory ----

    [Fact]
    public void FactoryReturnsAWorkingMonitorOnAnyHost()
    {
        using IPowerThermalMonitor monitor = PowerThermalMonitor.ForCurrentPlatform();

        Assert.NotNull(monitor.Current);

        // Off Windows there is no implementation, and the fallback must claim external power rather than
        // guessing battery — a wrong guess would cap quality on a desktop.
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsType<UnknownPowerThermalMonitor>(monitor);
            Assert.Equal(PowerSource.ExternalPower, monitor.Current.Source);
            Assert.False(monitor.Current.ThermalThrottling);
        }
    }

    // ---- the caps these signals are supposed to drive ----

    private static readonly SessionConfig Request =
        new(1920, 1080, 60, 15_000, VideoCodec.H264, LatencyMode.Balanced);

    [Fact]
    public void EnergySaver_ReducesQualityBeyondTheBatteryCap()
    {
        // The gap this closes: energy saver is precisely detectable and is explicit user intent, so it should
        // bite at least as hard as an inferred thermal state.
        var batteryOnly = new AdaptiveBandwidthController(Request);
        batteryOnly.ReportThermalPowerState(Battery(60));

        var withSaver = new AdaptiveBandwidthController(Request);
        withSaver.ReportThermalPowerState(Battery(60, energySaver: true));

        Assert.True(
            withSaver.RecommendBitrate().BitrateKbps < batteryOnly.RecommendBitrate().BitrateKbps,
            "energy saver should reduce quality below the plain battery cap");
    }

    [Fact]
    public void CriticalBattery_ReducesQualityBeyondTheBatteryCap()
    {
        var batteryOnly = new AdaptiveBandwidthController(Request);
        batteryOnly.ReportThermalPowerState(Battery(15));

        var criticalBattery = new AdaptiveBandwidthController(Request);
        criticalBattery.ReportThermalPowerState(Battery(3, critical: true));

        Assert.True(
            criticalBattery.RecommendBitrate().BitrateKbps < batteryOnly.RecommendBitrate().BitrateKbps);
    }

    [Fact]
    public void EachCapReasonIsNamedSpecifically()
    {
        // The overlay and the trace should say WHICH device limit applies, not just "to save battery".
        var thermal = new AdaptiveBandwidthController(Request);
        thermal.ReportThermalPowerState(Battery(80, thermal: true));
        Assert.Contains("thermally", thermal.LastDecisionReason, StringComparison.OrdinalIgnoreCase);

        var critical = new AdaptiveBandwidthController(Request);
        critical.ReportThermalPowerState(Battery(3, critical: true));
        Assert.Contains("critically low", critical.LastDecisionReason, StringComparison.OrdinalIgnoreCase);

        var saver = new AdaptiveBandwidthController(Request);
        saver.ReportThermalPowerState(Battery(70, energySaver: true));
        Assert.Contains("energy saver", saver.LastDecisionReason, StringComparison.OrdinalIgnoreCase);

        // A LOW battery, since a healthy one is deliberately no longer a cap at all — and the reason should
        // quote the actual percentage rather than a vague "to save battery".
        var plain = new AdaptiveBandwidthController(Request);
        plain.ReportThermalPowerState(Battery(20));
        Assert.Contains("battery is low (20%)", plain.LastDecisionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReturningToExternalPower_LiftsEveryDeviceCap()
    {
        var c = new AdaptiveBandwidthController(Request);
        c.ReportThermalPowerState(Battery(5, energySaver: true, critical: true));
        Assert.True(c.RecommendBitrate().Height <= AdaptiveBandwidthController.BatteryMaxHeight);

        c.ReportThermalPowerState(Ac());
        Assert.Equal(1080, c.RecommendBitrate().Height);
        Assert.Equal(15_000, c.RecommendBitrate().BitrateKbps);
    }
}
