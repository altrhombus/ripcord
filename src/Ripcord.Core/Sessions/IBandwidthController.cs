namespace Ripcord.Core.Sessions;

/// <param name="RoundTripTimeMs">Measured RTT in milliseconds, fractional — a wired link is well under 1 ms
/// and rounding that to an integer erases the difference between it and a Wi-Fi link.</param>
/// <summary>
/// One congestion-window observation of the link.
///
/// <para>
/// <paramref name="ObservedUnits"/> is deliberately NOT defaulted. A loss <em>ratio</em> without the count it was
/// computed from is not actionable, and the omission had a visible cost: at connect the first window held about
/// eight units, so a single lost one read as 12% loss and dropped a perfectly healthy 1080p60 stream a rung before
/// it had settled. A caller that forgets to supply the count should fail to compile rather than silently
/// reintroduce that.
/// </para>
/// </summary>
public sealed record NetworkSample(
    double RoundTripTimeMs,
    double PacketLossRatio,
    int JitterMs,
    long ObservedUnits);

public enum PowerSource
{
    Battery,
    ExternalPower,
}

/// <param name="ThermalThrottling">
/// True only when the host reports genuine thermal throttling. Windows exposes no reliable public API for this,
/// so a real implementation may never set it — see <c>WindowsPowerThermalMonitor</c>, which deliberately does not
/// guess from CPU clock ratios (they drop at idle too, so the "signal" fires on a menu screen).
/// </param>
/// <param name="EnergySaverActive">
/// True when the OS energy saver / battery saver is on. Unlike thermal state this is precisely detectable, and
/// it is an explicit statement of user intent — treat it as at least as strong a reason to reduce quality.
/// </param>
/// <param name="BatteryCritical">True when the host reports a critical battery level.</param>
public sealed record PowerState(
    PowerSource Source,
    int? BatteryPercent,
    bool ThermalThrottling,
    bool EnergySaverActive = false,
    bool BatteryCritical = false);

public sealed record BitrateDecision(int BitrateKbps, int Width, int Height, int Fps);

/// <summary>
/// Lives in Core (not per-protocol) so the same adaptive-bitrate and thermal-aware logic serves
/// both console families' sessions. Also the shared signal source for
/// <see cref="Ripcord.Core.Sessions.IStreamingSession"/>'s upscale-mode selection, so quality and
/// upscale mode are chosen jointly rather than independently contending for the same NPU/GPU
/// budget under thermal throttling.
/// </summary>
public interface IBandwidthController
{
    /// <summary>
    /// Plain-language reason for the most recent quality decision. On the interface because the layer that
    /// traces and displays decisions is not the layer that makes them — and "stepped down after 5.2% packet
    /// loss" is the part that makes a wobbling stream diagnosable.
    /// </summary>
    string LastDecisionReason { get; }

    void ReportNetworkSample(NetworkSample sample);

    void ReportThermalPowerState(PowerState state);

    BitrateDecision RecommendBitrate();
}
