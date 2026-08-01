namespace Ripcord.Core.Sessions;

/// <summary>What to send the console in a connection-quality report.</summary>
/// <param name="TargetBitrateKbps">The bitrate we would like it to encode at.</param>
/// <param name="RoundTripTimeMs">Our measured RTT, fractional milliseconds.</param>
/// <param name="LossPercent">Our measured A/V unit loss, 0..100.</param>
public readonly record struct ConnectionQualityReport(
    int TargetBitrateKbps,
    double RoundTripTimeMs,
    double LossPercent);

/// <summary>
/// Decides <em>when</em> to tell the console about our link, and what to say.
///
/// <para>
/// Two things make this worth separating from the send itself. First, the cadence matters: the statistics that
/// drive it arrive five times a second, and sending a control message at that rate would add avoidable uplink
/// traffic and give the console's own rate controller no time to settle between changes. Second, the interesting
/// event is a <em>change</em> in what we want — that should go out promptly — while unchanged state only needs a
/// periodic refresh so the console knows we are still measuring.
/// </para>
///
/// <para>Pure and clock-injected, so the cadence policy is testable without a console or a delay.</para>
/// </summary>
public sealed class ConnectionQualityReporter(TimeSpan? refreshInterval = null, TimeSpan? minimumSpacing = null)
{
    /// <summary>Resend unchanged state at least this often, so the console keeps a current view of the link.</summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Floor on the gap between any two reports, including change-driven ones. Without it, a bitrate ladder
    /// oscillating under load would emit a burst of control messages exactly when the uplink is least able to
    /// carry them.
    /// </summary>
    public static readonly TimeSpan DefaultMinimumSpacing = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Relative change in target bitrate treated as significant. Below this it is ladder noise, not a decision
    /// worth spending a control message on.
    /// </summary>
    public const double SignificantBitrateChange = 0.05;

    private readonly TimeSpan _refresh = refreshInterval ?? DefaultRefreshInterval;
    private readonly TimeSpan _minSpacing = minimumSpacing ?? DefaultMinimumSpacing;

    private DateTimeOffset _lastSent;
    private int _lastTargetKbps;
    private bool _haveSent;

    /// <summary>How many reports this instance has emitted. Diagnostics; also proves the path is live.</summary>
    public int ReportsSent { get; private set; }

    /// <summary>
    /// Returns the report to send now, or null to stay quiet. Call on every statistics sample.
    /// </summary>
    public ConnectionQualityReport? Next(
        int targetBitrateKbps,
        double roundTripTimeMs,
        double lossPercent,
        DateTimeOffset now)
    {
        if (targetBitrateKbps <= 0)
        {
            return null; // nothing meaningful to ask for yet
        }

        // The very first report goes out immediately: the console should learn our real RTT and loss as soon as
        // we have them rather than waiting out a refresh interval.
        if (!_haveSent)
        {
            return Emit(targetBitrateKbps, roundTripTimeMs, lossPercent, now);
        }

        if (now - _lastSent < _minSpacing)
        {
            return null;
        }

        bool targetChanged = IsSignificantChange(_lastTargetKbps, targetBitrateKbps);
        bool refreshDue = now - _lastSent >= _refresh;

        return targetChanged || refreshDue
            ? Emit(targetBitrateKbps, roundTripTimeMs, lossPercent, now)
            : null;
    }

    /// <summary>
    /// Whether the target moved enough to be a decision rather than noise. Public so the threshold is
    /// verifiable directly rather than only through the cadence behaviour it produces.
    /// </summary>
    public static bool IsSignificantChange(int previousKbps, int currentKbps)
    {
        if (previousKbps <= 0)
        {
            return true;
        }

        double delta = Math.Abs(currentKbps - previousKbps) / (double)previousKbps;
        return delta >= SignificantBitrateChange;
    }

    private ConnectionQualityReport Emit(int targetKbps, double rttMs, double lossPercent, DateTimeOffset now)
    {
        _lastSent = now;
        _lastTargetKbps = targetKbps;
        _haveSent = true;
        ReportsSent++;

        // Clamp loss to a sane percentage: a malformed sample must not put nonsense on the wire.
        return new ConnectionQualityReport(
            targetKbps,
            Math.Max(0, rttMs),
            Math.Clamp(lossPercent, 0, 100));
    }
}
