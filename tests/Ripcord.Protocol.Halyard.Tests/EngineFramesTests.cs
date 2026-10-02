using Ripcord.Core.Input;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The 2026-09-30 review: a DualSense pulled mid-press stayed pressed, because the merge kept every engine's last
/// frame for good. These hold the merge to forgetting an engine once its last device goes.
/// </summary>
public class EngineFramesTests
{
    private static ControllerStateFrame Held(ControllerButtons buttons, float leftX = 0f)
        => default(ControllerStateFrame) with { Buttons = buttons, LeftStickX = leftX, TimestampTicks = 1 };

    [Fact]
    public void APulledPad_StopsHoldingItsButton()
    {
        var frames = new EngineFrames();
        frames.Set("GameInput", Held(ControllerButtons.None));
        ControllerStateFrame both = frames.Set("DualSense", Held(ControllerButtons.South, leftX: 0.8f));
        Assert.True(both.Buttons.HasFlag(ControllerButtons.South));

        Assert.True(frames.Forget("DualSense", 2, out ControllerStateFrame after));

        Assert.Equal(ControllerButtons.None, after.Buttons);
        Assert.Equal(0f, after.LeftStickX);
    }

    [Fact]
    public void TheOtherPad_KeepsItsInput()
    {
        var frames = new EngineFrames();
        frames.Set("GameInput", Held(ControllerButtons.East));
        frames.Set("DualSense", Held(ControllerButtons.South));

        frames.Forget("DualSense", 2, out ControllerStateFrame after);

        Assert.Equal(ControllerButtons.East, after.Buttons);
    }

    [Fact]
    public void TheLastPadGoing_LeavesANeutralFrame()
    {
        var frames = new EngineFrames();
        frames.Set("DualSense", Held(ControllerButtons.South, leftX: -1f));

        Assert.True(frames.Forget("DualSense", 7, out ControllerStateFrame after));

        Assert.Equal(default(ControllerStateFrame) with { TimestampTicks = 7 }, after);
    }

    [Fact]
    public void ForgettingAnEngineWithNoFrame_ChangesNothing()
    {
        var frames = new EngineFrames();
        frames.Set("GameInput", Held(ControllerButtons.East));

        Assert.False(frames.Forget("DualSense", 2, out _));
        Assert.Equal(ControllerButtons.East, frames.Set("GameInput", Held(ControllerButtons.East)).Buttons);
    }

    [Fact]
    public void APadThatComesBack_IsMergedAgain()
    {
        var frames = new EngineFrames();
        frames.Set("DualSense", Held(ControllerButtons.South));
        frames.Forget("DualSense", 2, out _);

        Assert.Equal(ControllerButtons.North, frames.Set("DualSense", Held(ControllerButtons.North)).Buttons);
    }
}
