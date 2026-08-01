using Ripcord.Core.Sessions;

namespace Ripcord.Core.Power;

/// <summary>
/// Reports the host's power source, battery level, and thermal pressure. This is the signal that
/// <see cref="IBandwidthController.ReportThermalPowerState"/> has always accepted and nothing ever supplied —
/// which is why the app streamed identically on a plugged-in desktop and a throttling handheld on 20% battery.
///
/// <para>
/// On a handheld (the ROG Ally X class of target) this is not a nicety: sustained 1080p60 decode on battery is
/// the difference between a two-hour session and a one-hour session, and thermal throttling shows up to the
/// user as stutter they will blame on the network.
/// </para>
/// </summary>
public interface IPowerThermalMonitor : IDisposable
{
    /// <summary>The current power/thermal state. Cheap to read; implementations cache and poll.</summary>
    PowerState Current { get; }

    /// <summary>Raised when the state changes materially (source switch, threshold crossing, throttle onset).</summary>
    event Action<PowerState>? Changed;
}

/// <summary>
/// A monitor that always reports external power and no throttling. Used on hosts without a real
/// implementation, and in tests, so consumers never have to null-check the seam.
/// </summary>
public sealed class UnknownPowerThermalMonitor : IPowerThermalMonitor
{
    public PowerState Current { get; } = new(PowerSource.ExternalPower, null, ThermalThrottling: false);

    /// <summary>Never raised: this monitor's state is constant by definition.</summary>
    public event Action<PowerState>? Changed
    {
        add { }
        remove { }
    }

    public void Dispose()
    {
    }
}
