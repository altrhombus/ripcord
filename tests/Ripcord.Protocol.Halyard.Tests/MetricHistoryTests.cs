using Ripcord.Diagnostics;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The rolling metric window behind the diagnostics graph. The property that matters is chronological ordering
/// across the wrap point — get that wrong and the plot silently draws history in the wrong order, which looks
/// like real oscillation and would send you chasing a bug that isn't there.
/// </summary>
public class MetricHistoryTests
{
    [Fact]
    public void StartsEmpty()
    {
        var h = new MetricHistory(4);
        Assert.Equal(0, h.Count);
        Assert.Equal(0, h.Latest);
        Assert.Equal(0, h.Max());
    }

    [Fact]
    public void BeforeWrapping_ReturnsSamplesOldestFirst()
    {
        var h = new MetricHistory(4);
        h.Add(1);
        h.Add(2);
        h.Add(3);

        Assert.Equal(3, h.Count);
        Assert.Equal([1, 2, 3], Snapshot(h));
        Assert.Equal(3, h.Latest);
    }

    [Fact]
    public void AfterWrapping_StillReturnsSamplesOldestFirst()
    {
        var h = new MetricHistory(4);
        for (int i = 1; i <= 6; i++)
        {
            h.Add(i);
        }

        // Capacity 4, so 1 and 2 have aged out; the remainder must still be in order.
        Assert.Equal(4, h.Count);
        Assert.Equal([3, 4, 5, 6], Snapshot(h));
        Assert.Equal(6, h.Latest);
    }

    [Fact]
    public void ExactlyFull_IsOrderedCorrectly()
    {
        // The boundary case: Count == Capacity but the write cursor has just wrapped to 0.
        var h = new MetricHistory(3);
        h.Add(10);
        h.Add(20);
        h.Add(30);

        Assert.Equal([10, 20, 30], Snapshot(h));
    }

    [Fact]
    public void ManyWraps_KeepOrdering()
    {
        var h = new MetricHistory(5);
        for (int i = 0; i < 53; i++)
        {
            h.Add(i);
        }

        Assert.Equal([48, 49, 50, 51, 52], Snapshot(h));
    }

    [Fact]
    public void Max_ConsidersOnlyRetainedSamples()
    {
        var h = new MetricHistory(3);
        h.Add(100); // will age out
        h.Add(1);
        h.Add(2);
        h.Add(3);

        Assert.Equal(3, h.Max());
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var h = new MetricHistory(3);
        h.Add(5);
        h.Clear();

        Assert.Equal(0, h.Count);
        Assert.Equal(0, h.Latest);
    }

    [Theory]
    [InlineData(30, 60, 0.5)]
    [InlineData(0, 60, 0.0)]
    [InlineData(60, 60, 1.0)]
    [InlineData(120, 60, 1.0)]  // off-scale flattens at the top rather than escaping the plot
    [InlineData(-5, 60, 0.0)]   // and a negative can never draw below the baseline
    public void NormalisedAt_ClampsToTheUnitRange(double sample, double fullScale, double expected)
    {
        var h = new MetricHistory(4);
        h.Add(sample);
        Assert.Equal(expected, h.NormalisedAt(0, fullScale), precision: 6);
    }

    [Fact]
    public void NormalisedAt_ZeroFullScale_IsZeroRatherThanInfinity()
    {
        var h = new MetricHistory(4);
        h.Add(42);
        Assert.Equal(0, h.NormalisedAt(0, 0));
    }

    [Fact]
    public void IndexingOutOfRange_Throws()
    {
        var h = new MetricHistory(4);
        h.Add(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => h[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => h[-1]);
    }

    [Fact]
    public void CapacityBelowTwo_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricHistory(1));
    }

    private static double[] Snapshot(MetricHistory h)
    {
        var result = new double[h.Count];
        for (int i = 0; i < h.Count; i++)
        {
            result[i] = h[i];
        }

        return result;
    }
}
