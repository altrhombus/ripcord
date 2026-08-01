using Ripcord.Core.Sessions;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The cadence policy for CONNECTION_QUALITY reports. These matter because the input arrives 5x/second and the
/// output goes on the wire: too chatty and we add uplink traffic precisely when the link is struggling; too
/// quiet and the console's rate controller works from stale information.
/// </summary>
public class ConnectionQualityReporterTests
{
    private static DateTimeOffset T0 => new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstReport_IsSentImmediately()
    {
        // The console should learn our real RTT/loss as soon as we have them, not after a refresh interval.
        var r = new ConnectionQualityReporter();
        ConnectionQualityReport? report = r.Next(10_000, 12.5, 0.5, T0);

        Assert.NotNull(report);
        Assert.Equal(10_000, report!.Value.TargetBitrateKbps);
        Assert.Equal(12.5, report.Value.RoundTripTimeMs);
        Assert.Equal(0.5, report.Value.LossPercent);
    }

    [Fact]
    public void UnchangedState_IsNotResentUntilTheRefreshInterval()
    {
        var r = new ConnectionQualityReporter();
        r.Next(10_000, 10, 0, T0);

        // Well past the minimum spacing but short of the refresh: nothing to say.
        Assert.Null(r.Next(10_000, 10, 0, T0 + TimeSpan.FromSeconds(1)));

        // Refresh due.
        Assert.NotNull(r.Next(10_000, 10, 0, T0 + ConnectionQualityReporter.DefaultRefreshInterval));
    }

    [Fact]
    public void SignificantTargetChange_IsReportedPromptly()
    {
        var r = new ConnectionQualityReporter();
        r.Next(10_000, 10, 0, T0);

        // A ladder step down should not wait out the refresh interval — it is the whole point of the message.
        ConnectionQualityReport? report = r.Next(
            7_000, 10, 3.0, T0 + ConnectionQualityReporter.DefaultMinimumSpacing);

        Assert.NotNull(report);
        Assert.Equal(7_000, report!.Value.TargetBitrateKbps);
    }

    [Fact]
    public void MinimumSpacing_SuppressesABurstOfChanges()
    {
        // An oscillating ladder must not turn into a control-message storm on an already-struggling uplink.
        var r = new ConnectionQualityReporter();
        r.Next(10_000, 10, 0, T0);

        Assert.Null(r.Next(5_000, 10, 8, T0 + TimeSpan.FromMilliseconds(50)));
        Assert.Null(r.Next(3_000, 10, 9, T0 + TimeSpan.FromMilliseconds(100)));
        Assert.Equal(1, r.ReportsSent);
    }

    [Fact]
    public void InsignificantTargetDrift_IsNotReported()
    {
        var r = new ConnectionQualityReporter();
        r.Next(10_000, 10, 0, T0);

        // 2% is ladder noise, not a decision.
        Assert.Null(r.Next(10_200, 10, 0, T0 + TimeSpan.FromMilliseconds(600)));
        Assert.Equal(1, r.ReportsSent);
    }

    [Fact]
    public void NoTargetYet_ProducesNothing()
    {
        var r = new ConnectionQualityReporter();
        Assert.Null(r.Next(0, 10, 0, T0));
        Assert.Equal(0, r.ReportsSent);
    }

    [Fact]
    public void LossIsClampedToAValidPercentage()
    {
        // A malformed sample must not put nonsense on the wire.
        var r = new ConnectionQualityReporter();
        ConnectionQualityReport? report = r.Next(10_000, -5, 250, T0);

        Assert.NotNull(report);
        Assert.Equal(0, report!.Value.RoundTripTimeMs);
        Assert.Equal(100, report.Value.LossPercent);
    }

    [Theory]
    [InlineData(10_000, 10_000, false)]
    [InlineData(10_000, 10_400, false)] // 4% — below threshold
    [InlineData(10_000, 10_600, true)]  // 6% — above threshold
    [InlineData(10_000, 5_000, true)]
    [InlineData(0, 10_000, true)]       // no previous value is always significant
    public void SignificanceThreshold(int previous, int current, bool expected)
    {
        Assert.Equal(expected, ConnectionQualityReporter.IsSignificantChange(previous, current));
    }

    [Fact]
    public void SustainedStableLink_ReportsOnlyOnRefreshes()
    {
        // Over 10 seconds of steady state at 5 samples/second (50 calls), the reporter should emit roughly one
        // report per refresh interval — not 50.
        var r = new ConnectionQualityReporter();
        for (int i = 0; i < 50; i++)
        {
            r.Next(10_000, 8, 0.1, T0 + TimeSpan.FromMilliseconds(200 * i));
        }

        int expected = (int)(TimeSpan.FromSeconds(10) / ConnectionQualityReporter.DefaultRefreshInterval);
        Assert.InRange(r.ReportsSent, expected - 1, expected + 2);
    }
}
