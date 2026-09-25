using Ripcord.Presentation.Sessions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Where the instrument panel goes. Real display sizes throughout, because the whole point of the rule is
/// that it holds on hardware nobody developing it is sitting in front of.
/// </summary>
public class HudPlacementTests
{
    private static HudLayout For(double w, double h, double vw = 1920, double vh = 1080)
        => HudPlacement.For(w, h, vw, vh);

    [Fact]
    public void FullscreenSixteenByNineHasNoDeadSpaceSoItOverlays()
    {
        // The only case that covers picture, and it covers it because there is genuinely nowhere else.
        Assert.Equal(DiagnosticsPlacement.Overlay, For(1920, 1080).Placement);
        Assert.Equal(DiagnosticsPlacement.Overlay, For(2560, 1440).Placement);
        Assert.Equal(DiagnosticsPlacement.Overlay, For(3840, 2160).Placement);
    }

    [Theory]
    [InlineData(3440, 1440)] // 21:9 ultrawide - 440 per side
    [InlineData(2560, 1080)] // 21:9 at 1080p - 320 per side
    [InlineData(5120, 1440)] // 32:9 superwide
    public void AnUltrawideGivesTheRailARealPillar(double width, double height)
    {
        HudLayout layout = For(width, height);

        Assert.Equal(DiagnosticsPlacement.Rail, layout.Placement);
        Assert.True(layout.PillarWidth >= HudPlacement.RailMinimumWidth);
    }

    [Fact]
    public void TheUltrawideArithmeticIsWhatItClaimsToBe()
    {
        // 3440x1440 showing a 16:9 picture: the picture is 2560 wide, leaving 880 split between two bars.
        HudLayout layout = For(3440, 1440);

        Assert.Equal(440, layout.PillarWidth, precision: 6);
        Assert.Equal(0, layout.BarHeight, precision: 6);
    }

    [Fact]
    public void ATallWindowPutsTheSheetInTheLetterboxBar()
    {
        // A 16:10 window is taller than the picture, so the bar is where the free space is.
        HudLayout layout = For(1920, 1600);

        Assert.Equal(DiagnosticsPlacement.Sheet, layout.Placement);
        Assert.True(layout.BarHeight >= HudPlacement.SheetMinimumHeight);
        Assert.Equal(0, layout.PillarWidth, precision: 6);
    }

    [Fact]
    public void AHandheldsThinLetterboxIsNotEnoughAndItOverlays()
    {
        // A 16:9 picture in a 16:10 window: 1280x720 shown, so 40px of bar at each end. Forty pixels of
        // instruments would be neither readable nor free, so the honest answer is to overlay.
        HudLayout layout = For(1280, 800);

        Assert.Equal(DiagnosticsPlacement.Overlay, layout.Placement);
        Assert.Equal(40, layout.BarHeight, precision: 6);
    }

    [Fact]
    public void ASlightlyWideWindowIsNotAnUltrawide()
    {
        // Just under the rail minimum: the panel would have to wrap its own labels, at which point
        // overlaying is better because at least it is legible.
        HudLayout layout = For(1920 + (2 * 290), 1080);

        Assert.Equal(DiagnosticsPlacement.Overlay, layout.Placement);
        Assert.Equal(290, layout.PillarWidth, precision: 6);
    }

    [Theory]
    [InlineData(3440, 1440)]
    [InlineData(1920, 1600)]
    [InlineData(1280, 800)]
    [InlineData(1920, 1080)]
    [InlineData(800, 2000)]
    public void AWindowIsPillarboxedOrLetterboxedButNeverBoth(double width, double height)
    {
        // The picture is scaled uniformly, so the tighter axis fills the viewport exactly and the other gets
        // all the slack. This is why the order the two are tested in is not a preference: there is never a
        // tie to break, and a future reader "fixing" one would be fixing nothing.
        HudLayout layout = For(width, height);

        Assert.True(
            layout.PillarWidth == 0 || layout.BarHeight == 0,
            $"expected one bar to be zero, got pillar {layout.PillarWidth} and bar {layout.BarHeight}");
    }

    [Fact]
    public void APictureThatIsNotSixteenByNineIsHandledOnItsOwnAspect()
    {
        // The rule is about the letterbox the picture actually leaves, not about an assumed 16:9 source.
        // A 4:3 picture on a 1080p display leaves 240 per side - wide, but under the rail minimum, so it
        // still overlays.
        Assert.Equal(DiagnosticsPlacement.Overlay, HudPlacement.For(1920, 1080, 1440, 1080).Placement);
        Assert.Equal(240, HudPlacement.For(1920, 1080, 1440, 1080).PillarWidth, precision: 6);

        // A square picture leaves 420, which does clear it.
        HudLayout layout = HudPlacement.For(1920, 1080, 1080, 1080);

        Assert.Equal(DiagnosticsPlacement.Rail, layout.Placement);
        Assert.Equal(420, layout.PillarWidth, precision: 6);
    }

    [Fact]
    public void NoFrameYetMeansNoLetterboxToReasonAbout()
    {
        // Guessing at a placement before the first frame would move the panel the moment the picture
        // disagreed, which is worse than starting where it will stay.
        Assert.Equal(DiagnosticsPlacement.Overlay, HudPlacement.For(3440, 1440, 0, 0).Placement);
        Assert.Equal(DiagnosticsPlacement.Overlay, HudPlacement.For(3440, 1440, 1920, 0).Placement);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(double.NaN, 1080)]
    [InlineData(1920, double.PositiveInfinity)]
    public void ADegenerateViewportNeverThrows(double width, double height)
    {
        // ActualWidth is 0 until the first layout pass, and a page mid-teardown can report worse.
        Assert.Equal(DiagnosticsPlacement.Overlay, For(width, height).Placement);
    }
}
