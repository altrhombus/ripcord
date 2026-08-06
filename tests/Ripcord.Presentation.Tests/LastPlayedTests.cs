using Ripcord.Presentation.Consoles;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The last-played caption. Every threshold below is a place this can be off by one unit, and none of them were
/// testable before the clock was injected — the logic sat in a property reading DateTimeOffset.UtcNow.
/// </summary>
public class LastPlayedTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static string? At(TimeSpan ago) => LastPlayed.Describe(Now - ago, Now);

    [Fact]
    public void NeverPlayed_IsNull_SoTheCardShowsNothingRatherThanNever()
        => Assert.Null(LastPlayed.Describe(null, Now));

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    [InlineData(119)]     // last second before the two-minute boundary
    public void UnderTwoMinutes_ReadsAsJustNow(int seconds)
        => Assert.Equal("Played just now", At(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void AtTwoMinutes_SwitchesToMinutes()
        => Assert.Equal("Played 2 min ago", At(TimeSpan.FromMinutes(2)));

    [Fact]
    public void LastMinuteBeforeAnHour_IsStillMinutes()
        => Assert.Equal("Played 59 min ago", At(TimeSpan.FromMinutes(59)));

    [Fact]
    public void AtOneHour_SwitchesToHours_Singular()
        => Assert.Equal("Played 1 hour ago", At(TimeSpan.FromHours(1)));

    [Fact]
    public void BeyondOneHour_IsPlural()
        => Assert.Equal("Played 2 hours ago", At(TimeSpan.FromHours(2)));

    [Fact]
    public void LastHourBeforeADay_IsStillHours()
        => Assert.Equal("Played 23 hours ago", At(TimeSpan.FromHours(23)));

    [Fact]
    public void AtOneDay_SwitchesToDays_Singular()
        => Assert.Equal("Played 1 day ago", At(TimeSpan.FromDays(1)));

    [Fact]
    public void BeyondOneDay_IsPlural()
        => Assert.Equal("Played 3 days ago", At(TimeSpan.FromDays(3)));

    [Fact]
    public void LastDayBeforeAWeek_IsStillDays()
        => Assert.Equal("Played 6 days ago", At(TimeSpan.FromDays(6)));

    [Fact]
    public void AtOneWeek_SwitchesToAnAbsoluteDate()
    {
        // Past a week, "Played 9 days ago" stops being useful and a date starts being. Asserted loosely on
        // purpose: the exact rendering is culture-dependent, but it must be a date rather than an elapsed span.
        string? described = At(TimeSpan.FromDays(7));

        Assert.NotNull(described);
        Assert.StartsWith("Played ", described);
        Assert.DoesNotContain("ago", described);
    }

    [Fact]
    public void AClockThatWentBackwards_DoesNotProduceANegativeSpan()
    {
        // An NTP correction, or a record written on a machine whose clock is ahead. "Played -3 min ago" would be
        // worse than a small inaccuracy, and the first branch catches it because a negative span is also < 2.
        Assert.Equal("Played just now", LastPlayed.Describe(Now.AddMinutes(3), Now));
    }
}
