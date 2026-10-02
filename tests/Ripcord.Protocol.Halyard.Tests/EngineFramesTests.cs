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
    public void TheActiveEngine_IsTheOneLastPressed()
    {
        var frames = new EngineFrames();
        Assert.Null(frames.Active);

        frames.Set("GameInput", Held(ControllerButtons.None));
        Assert.Null(frames.Active);   // a pad reporting at rest is not a pad in use

        frames.Set("DualSense", Held(ControllerButtons.South));
        Assert.Equal("DualSense", frames.Active);

        frames.Set("GameInput", Held(ControllerButtons.North));
        Assert.Equal("GameInput", frames.Active);

        // The DualSense goes on reporting the button it still holds; that is not a new press.
        frames.Set("DualSense", Held(ControllerButtons.South));
        Assert.Equal("GameInput", frames.Active);

        frames.Set("DualSense", Held(ControllerButtons.None, leftX: 0.9f));
        Assert.Equal("DualSense", frames.Active);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnePadSeenByTwoEngines_GoesToTheFirstInPrecedence_WhicheverReportsFirst(bool hidFirst)
    {
        var frames = new EngineFrames("DualSense", "GameInput");
        frames.Set("DualSense", Held(ControllerButtons.None));
        frames.Set("GameInput", Held(ControllerButtons.None));

        string[] order = hidFirst ? ["DualSense", "GameInput"] : ["GameInput", "DualSense"];
        foreach (string engine in order)
        {
            frames.Set(engine, Held(ControllerButtons.South));
        }

        Assert.Equal("DualSense", frames.Active);
    }

    [Fact]
    public void APressOnlyTheLaterEngineSees_IsThatEngines()
    {
        // The Xbox pad: GameInput alone reports it, so precedence does not come into it.
        var frames = new EngineFrames("DualSense", "GameInput");
        frames.Set("DualSense", Held(ControllerButtons.South));
        frames.Set("GameInput", Held(ControllerButtons.North));

        Assert.Equal("GameInput", frames.Active);
    }

    [Fact]
    public void ADriftingStick_DoesNotClaimThePad()
    {
        var frames = new EngineFrames();
        frames.Set("GameInput", Held(ControllerButtons.South));

        frames.Set("DualSense", Held(ControllerButtons.None, leftX: 0.12f));
        frames.Set("DualSense", Held(ControllerButtons.None, leftX: 0.18f));

        Assert.Equal("GameInput", frames.Active);
    }

    [Fact]
    public void AForgottenEngine_StaysActive()
    {
        // A pad going to sleep must not relabel the screen.
        var frames = new EngineFrames();
        frames.Set("DualSense", Held(ControllerButtons.South));

        frames.Forget("DualSense", 2, out _);

        Assert.Equal("DualSense", frames.Active);
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
