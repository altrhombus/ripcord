using Ripcord.Core.Input;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Several gamepads read through one engine. The failure these guard against was measured on hardware on
/// 2026-08-06: with a DualSense and an Xbox pad attached, GameInput was asked for "the latest reading from any
/// gamepad" and the Xbox pad never produced a single button press. Reading each device is the native half of the
/// fix; this is the half that decides what the per-device readings add up to.
/// </summary>
public class GamepadSetTests
{
    private static ControllerStateFrame Frame(
        ControllerButtons buttons = ControllerButtons.None, float leftX = 0, long ticks = 0)
        => new(ticks, buttons, leftX, 0, 0, 0, 0, 0, null, null, null);

    private static GamepadReading Pad(ulong id, ControllerButtons buttons = ControllerButtons.None, float leftX = 0)
        => new(id, Frame(buttons, leftX));

    [Fact]
    public void ASecondPadsButtonsReachTheMergedFrame()
    {
        // The reported bug in its smallest form: one pad idle and reporting constantly, the other pressing South.
        // The pressing pad is listed first, so a "last reading wins" rule fails this rather than passing it.
        var set = new GamepadSet();

        GamepadSetUpdate update = set.Update([Pad(2, ControllerButtons.South), Pad(1)]);

        Assert.Equal(ControllerButtons.South, update.Merged!.Value.Buttons);
    }

    [Fact]
    public void AHeldButtonStaysHeldWhileTheOtherPadReports()
    {
        // Why the engine merges rather than publishing a frame per pad: the composite keeps one frame per engine,
        // so per-pad frames through one engine would release this button every time pad 1 reported.
        var set = new GamepadSet();

        set.Update([Pad(2, ControllerButtons.East), Pad(1)]);
        GamepadSetUpdate second = set.Update([Pad(2, ControllerButtons.East), Pad(1)]);

        Assert.Equal(ControllerButtons.East, second.Merged!.Value.Buttons);
    }

    [Fact]
    public void AxesTakeWhicheverPadIsFurtherFromCentre()
    {
        var set = new GamepadSet();

        GamepadSetUpdate update = set.Update([Pad(1, leftX: 0.1f), Pad(2, leftX: -0.8f)]);

        Assert.Equal(-0.8f, update.Merged!.Value.LeftStickX);
    }

    [Fact]
    public void EachPadIsAnnouncedOnceWhenItAppears()
    {
        var set = new GamepadSet();

        GamepadSetUpdate first = set.Update([Pad(1)]);
        GamepadSetUpdate second = set.Update([Pad(1), Pad(2)]);
        GamepadSetUpdate third = set.Update([Pad(1), Pad(2)]);

        Assert.Equal([1ul], first.Connected);
        Assert.Equal([2ul], second.Connected);
        Assert.Empty(third.Connected);
        Assert.Empty(third.Disconnected);
    }

    [Fact]
    public void APadThatStopsReportingIsDisconnected_AndTheOtherKeepsWorking()
    {
        // The second half of the 2026-08-06 report: removing the DualSense did not bring the Xbox pad back, because
        // the single read was never re-bound. Per-device reads have nothing to re-bind.
        var set = new GamepadSet();
        set.Update([Pad(1), Pad(2)]);

        GamepadSetUpdate update = set.Update([Pad(2, ControllerButtons.North)]);

        Assert.Equal([1ul], update.Disconnected);
        Assert.Equal(ControllerButtons.North, update.Merged!.Value.Buttons);
        Assert.Equal([2ul], set.Connected);
    }

    [Fact]
    public void NoPadsMeansNoFrame_AndEveryPadIsDisconnected()
    {
        var set = new GamepadSet();
        set.Update([Pad(1), Pad(2)]);

        GamepadSetUpdate update = set.Update([]);

        Assert.Null(update.Merged);
        Assert.Equal([1ul, 2ul], update.Disconnected.Order());
    }

    [Fact]
    public void AReplugIsANewConnection()
    {
        // The reader never reuses an id, so a pad unplugged and plugged back in is a disconnect and a connect.
        var set = new GamepadSet();
        set.Update([Pad(1)]);

        GamepadSetUpdate update = set.Update([Pad(3)]);

        Assert.Equal([3ul], update.Connected);
        Assert.Equal([1ul], update.Disconnected);
    }

    [Fact]
    public void TheSameDeviceTwiceInOnePollCountsOnce()
    {
        var set = new GamepadSet();

        GamepadSetUpdate update = set.Update([Pad(1, ControllerButtons.South), Pad(1, ControllerButtons.South)]);

        Assert.Equal([1ul], update.Connected);
        Assert.Equal(ControllerButtons.South, update.Merged!.Value.Buttons);
    }
}
