using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ripcord.Core.Sessions;

namespace Ripcord.Core.Power;

/// <summary>
/// Windows power state via <c>GetSystemPowerStatus</c>, polled on a timer.
///
/// <para>
/// <b>On thermal state, deliberately honest:</b> Windows exposes no reliable public API for "am I thermally
/// throttling". The tempting proxy — comparing current CPU MHz against maximum via
/// <c>CallNtPowerInformation</c> — is not a throttling signal at all: modern CPUs sit far below their maximum
/// clock whenever they are idle, so it would report throttling on a static menu screen and needlessly halve
/// stream quality. This monitor therefore leaves <see cref="PowerState.ThermalThrottling"/> false and instead
/// reports two signals that <em>are</em> precisely detectable and genuinely actionable: the OS energy saver
/// (explicit user intent to conserve) and a critical battery level.
/// </para>
///
/// <para>
/// P/Invoke rather than the WinRT <c>Windows.System.Power</c> projections so this can live in Core alongside
/// the DPAPI protector and device-identity reader, keeping the assembly at a neutral <c>net10.0</c> target with
/// no project references. Degrades to <see cref="UnknownPowerThermalMonitor"/> behaviour if the call fails.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPowerThermalMonitor : IPowerThermalMonitor
{
    /// <summary>
    /// Poll interval. Power transitions are human-scale events (plugging in, a battery threshold), so polling
    /// often would burn wakeups — itself a battery cost — for no benefit.
    /// </summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private PowerState _current;

    public WindowsPowerThermalMonitor(TimeSpan? pollInterval = null)
    {
        _current = Read() ?? UnknownState;
        TimeSpan interval = pollInterval ?? DefaultPollInterval;
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    private static PowerState UnknownState { get; } =
        new(PowerSource.ExternalPower, null, ThermalThrottling: false);

    public PowerState Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<PowerState>? Changed;

    private void Poll()
    {
        PowerState? read = Read();
        if (read is null)
        {
            return;
        }

        lock (_gate)
        {
            // Only publish material transitions: the controller reacts to every report, and a battery
            // percentage ticking 61 -> 60 is not a reason to re-evaluate stream quality.
            if (!PowerThermalMonitor.IsMaterialChange(_current, read))
            {
                _current = read; // keep the percentage fresh without notifying
                return;
            }

            _current = read;
        }

        Changed?.Invoke(read);
    }

    private static PowerState? Read()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status))
        {
            return null;
        }

        // ACLineStatus: 0 = running on battery, 1 = on AC, 255 = unknown. Treat unknown as AC so an
        // undetectable host is never quality-capped on a guess.
        PowerSource source = status.ACLineStatus == 0 ? PowerSource.Battery : PowerSource.ExternalPower;

        int? percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;

        // BatteryFlag bit 2 (value 4) = critical. Bit 7 (128) = no system battery, in which case the flag
        // bits carry no meaning.
        bool hasBattery = (status.BatteryFlag & BatteryFlagNoBattery) == 0 && status.BatteryFlag != 255;
        bool critical = hasBattery && (status.BatteryFlag & BatteryFlagCritical) != 0;

        // SystemStatusFlag bit 0 = energy saver on (Windows 10+).
        bool energySaver = (status.SystemStatusFlag & 1) != 0;

        return new PowerState(
            source,
            percent,
            ThermalThrottling: false, // see the class remarks: no trustworthy source for this
            EnergySaverActive: energySaver,
            BatteryCritical: critical);
    }

    public void Dispose() => _timer.Dispose();

    private const byte BatteryFlagCritical = 0x04;
    private const byte BatteryFlagNoBattery = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}

/// <summary>Selects the best available power monitor for the current host, and hosts the shared debounce policy.</summary>
public static class PowerThermalMonitor
{
    public static IPowerThermalMonitor ForCurrentPlatform(TimeSpan? pollInterval = null)
        => OperatingSystem.IsWindows()
            ? new WindowsPowerThermalMonitor(pollInterval)
            : new UnknownPowerThermalMonitor();

    /// <summary>
    /// Whether a new reading differs enough to be worth publishing: a change of power source, energy-saver,
    /// critical-battery or thermal state, or a battery percentage crossing a 10% boundary.
    ///
    /// <para>
    /// Lives here rather than on the Windows implementation because it is entirely platform-independent — a
    /// future libupower or IOKit monitor wants exactly the same rule — and because consumers re-evaluate stream
    /// quality on every report, so a battery reading ticking 61 to 60 must not trigger one.
    /// </para>
    /// </summary>
    public static bool IsMaterialChange(PowerState previous, PowerState current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        if (previous.Source != current.Source
            || previous.EnergySaverActive != current.EnergySaverActive
            || previous.BatteryCritical != current.BatteryCritical
            || previous.ThermalThrottling != current.ThermalThrottling)
        {
            return true;
        }

        return (previous.BatteryPercent, current.BatteryPercent) switch
        {
            (null, not null) or (not null, null) => true,
            (int a, int b) => a / 10 != b / 10,
            _ => false,
        };
    }
}
