using Ripcord.Presentation.Settings;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The scaling policy. Testable without a window because the arithmetic deliberately lives in the portable
/// layer — see <see cref="UiScale"/> for why the split is where it is.
/// </summary>
public class UiScaleTests
{
    [Fact]
    public void Off_ChangesNothing()
    {
        Assert.Equal(1.0, UiScale.Effective(largeUiScale: false, osTextScaleFactor: 1.0));
    }

    [Fact]
    public void On_IsTheLargeMultiplier()
    {
        Assert.Equal(UiScale.LargeMultiplier, UiScale.Effective(largeUiScale: true, osTextScaleFactor: 1.0));
    }

    /// <summary>
    /// The bug this feature could most easily have shipped, guarded directly.
    ///
    /// <para>The written plan multiplied the app switch by the OS text scale. Microsoft's documentation says
    /// WinUI already applies that factor itself, so doing it again would give a user at 150% a 225% Ripcord —
    /// the accessibility setting breaking the layout it was meant to make readable. While
    /// <see cref="UiScale.AppliesOsTextScaleItself"/> is false the OS reading must not reach the result at
    /// all, and this asserts that by feeding it values that would be impossible to miss.</para>
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.25)]
    public void WhileThePlatformOwnsOsScaling_TheOsFactorIsNotAppliedTwice(double os)
    {
        // Asserted against the switch rather than skipped when it flips, so this keeps testing something
        // real whichever way the hardware check goes: either the OS factor is absent from the result, or it
        // is present exactly once. "Once" is the whole proposition, and a skipped test asserts neither half.
        double expectedOff = UiScale.AppliesOsTextScaleItself ? os : 1.0;

        Assert.Equal(expectedOff, UiScale.Effective(largeUiScale: false, osTextScaleFactor: os));
        Assert.Equal(
            Math.Min(expectedOff * UiScale.LargeMultiplier, UiScale.Maximum),
            UiScale.Effective(largeUiScale: true, osTextScaleFactor: os));
    }

    [Fact]
    public void NeverGoesBelowOne()
    {
        // An app-level "compact" that undoes an accessibility choice is not on offer, so even a nonsense
        // reading from below cannot shrink the interface.
        Assert.Equal(1.0, UiScale.Effective(largeUiScale: false, osTextScaleFactor: 0.5));
        Assert.Equal(1.0, UiScale.Effective(largeUiScale: false, osTextScaleFactor: 0));
    }

    [Fact]
    public void NeverExceedsThePlatformCeiling()
    {
        Assert.True(UiScale.Effective(largeUiScale: true, osTextScaleFactor: 2.25) <= UiScale.Maximum);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonsenseReadingIsNotAnInvisibleInterface(double os)
    {
        double result = UiScale.Effective(largeUiScale: false, osTextScaleFactor: os);

        Assert.True(double.IsFinite(result));
        Assert.InRange(result, UiScale.Minimum, UiScale.Maximum);
    }

    [Fact]
    public void SizesRoundToWholePixels()
    {
        // 13 * 1.3 = 16.9. A half-pixel border lands across two rows of physical pixels and renders grey and
        // soft - most visible on the hairlines a larger UI exists to make easier to see.
        Assert.Equal(17, UiScale.Scaled(13, 1.3));
        Assert.Equal(18, UiScale.Scaled(14, 1.3));
        Assert.Equal(Math.Floor(UiScale.Scaled(14, 1.3)), UiScale.Scaled(14, 1.3));
    }

    [Fact]
    public void ASmallSizeDoesNotRoundAwayToNothing()
    {
        // The 2px spacing step at a factor that would otherwise floor it out of existence.
        Assert.True(UiScale.Scaled(2, 1.0) >= 1);
        Assert.True(UiScale.Scaled(1, 1.0) >= 1);
    }

    [Fact]
    public void ZeroAndNegativeSizesArePassedThrough()
    {
        // A zero margin is a value someone chose, not a size to grow; scaling it would invent spacing.
        Assert.Equal(0, UiScale.Scaled(0, 2.0));
        Assert.Equal(-1, UiScale.Scaled(-1, 2.0));
    }
}
