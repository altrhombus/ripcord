using Ripcord.Core.Input;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Gamepad navigation timing, which used to be observable only by holding a real pad and counting.
///
/// <para>
/// Every case here corresponds to a complaint the code was written for: focus moving once and stopping,
/// focus moving twice on one press, a button firing continuously while held, and a resting stick walking
/// down a list on its own.
/// </para>
/// </summary>
public class NavIntentReaderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static ControllerStateFrame Frame(
        ControllerButtons buttons = ControllerButtons.None,
        float stickX = 0,
        float stickY = 0)
        => new() { Buttons = buttons, LeftStickX = stickX, LeftStickY = stickY };

    // ---- directions ----------------------------------------------------------------------------

    [Fact]
    public void ACentredStickAsksForNothing()
        => Assert.Equal(NavIntent.None, new NavIntentReader().Read(Frame(), T0));

    [Theory]
    [InlineData(ControllerButtons.DPadUp, NavDirection.Up)]
    [InlineData(ControllerButtons.DPadDown, NavDirection.Down)]
    [InlineData(ControllerButtons.DPadLeft, NavDirection.Left)]
    [InlineData(ControllerButtons.DPadRight, NavDirection.Right)]
    public void TheDPadMovesImmediately(ControllerButtons button, NavDirection expected)
        => Assert.Equal(expected, new NavIntentReader().Read(Frame(button), T0).Direction);

    [Fact]
    public void AStickInsideTheDeadzoneIsIgnored()
    {
        // The complaint this is for: a pad whose stick rests slightly off-centre walked focus down a list on
        // its own, with nobody touching it.
        var reader = new NavIntentReader { StickDeadzone = 0.5 };

        Assert.Equal(NavDirection.None, reader.Read(Frame(stickY: 0.49f), T0).Direction);
        Assert.Equal(NavDirection.Up, reader.Read(Frame(stickY: 0.51f), T0).Direction);
    }

    [Fact]
    public void TheDeadzoneIsSettableWhileRunning()
    {
        // It is a user setting, and someone adjusting it wants to feel the change now rather than on restart.
        var reader = new NavIntentReader { StickDeadzone = 0.8 };
        Assert.Equal(NavDirection.None, reader.Read(Frame(stickY: 0.6f), T0).Direction);

        reader.StickDeadzone = 0.5;
        Assert.Equal(NavDirection.Up, reader.Read(Frame(stickY: 0.6f), T0).Direction);
    }

    // ---- auto-repeat ---------------------------------------------------------------------------

    [Fact]
    public void AHeldDirectionIsSilentUntilTheDelayElapses()
    {
        // The initial pause is what stops an intended single step becoming two: a pad is polled far faster
        // than a person can tap, so without it every press moved at least twice.
        var reader = new NavIntentReader(
            repeatDelay: TimeSpan.FromMilliseconds(400), repeatInterval: TimeSpan.FromMilliseconds(120));

        Assert.Equal(NavDirection.Down, reader.Read(Frame(ControllerButtons.DPadDown), T0).Direction);

        foreach (int ms in new[] { 16, 100, 250, 399 })
        {
            Assert.Equal(
                NavDirection.None,
                reader.Read(Frame(ControllerButtons.DPadDown), T0.AddMilliseconds(ms)).Direction);
        }

        Assert.Equal(
            NavDirection.Down,
            reader.Read(Frame(ControllerButtons.DPadDown), T0.AddMilliseconds(400)).Direction);
    }

    [Fact]
    public void AfterTheDelayItRepeatsAtTheInterval()
    {
        var reader = new NavIntentReader(
            repeatDelay: TimeSpan.FromMilliseconds(400), repeatInterval: TimeSpan.FromMilliseconds(120));

        reader.Read(Frame(ControllerButtons.DPadDown), T0);
        reader.Read(Frame(ControllerButtons.DPadDown), T0.AddMilliseconds(400));

        // Between ticks: nothing, even though the pad is reporting the direction on every frame.
        Assert.Equal(
            NavDirection.None,
            reader.Read(Frame(ControllerButtons.DPadDown), T0.AddMilliseconds(500)).Direction);

        Assert.Equal(
            NavDirection.Down,
            reader.Read(Frame(ControllerButtons.DPadDown), T0.AddMilliseconds(520)).Direction);
    }

    [Fact]
    public void ChangingDirectionMovesAtOnceAndRestartsTheDelay()
    {
        // Turning a corner should feel immediate, not inherit the repeat cadence of the direction you left.
        var reader = new NavIntentReader(
            repeatDelay: TimeSpan.FromMilliseconds(400), repeatInterval: TimeSpan.FromMilliseconds(120));

        reader.Read(Frame(ControllerButtons.DPadDown), T0);
        reader.Read(Frame(ControllerButtons.DPadDown), T0.AddMilliseconds(400));

        Assert.Equal(
            NavDirection.Right,
            reader.Read(Frame(ControllerButtons.DPadRight), T0.AddMilliseconds(410)).Direction);

        Assert.Equal(
            NavDirection.None,
            reader.Read(Frame(ControllerButtons.DPadRight), T0.AddMilliseconds(700)).Direction);
    }

    [Fact]
    public void ReleasingAndPressingAgainMovesImmediately()
    {
        var reader = new NavIntentReader();

        reader.Read(Frame(ControllerButtons.DPadDown), T0);
        reader.Read(Frame(), T0.AddMilliseconds(50));

        Assert.Equal(
            NavDirection.Down,
            reader.Read(Frame(ControllerButtons.DPadDown), T0.AddMilliseconds(100)).Direction);
    }

    // ---- button edges --------------------------------------------------------------------------

    [Fact]
    public void AcceptFiresOnceNotForAsLongAsItIsHeld()
    {
        // Frames are absolute state, so a held button appears in every one. Acting on presence rather than on
        // the rising edge would activate the focused element continuously.
        var reader = new NavIntentReader();

        Assert.True(reader.Read(Frame(ControllerButtons.South), T0).Accept);
        Assert.False(reader.Read(Frame(ControllerButtons.South), T0.AddMilliseconds(16)).Accept);
        Assert.False(reader.Read(Frame(ControllerButtons.South), T0.AddSeconds(5)).Accept);

        reader.Read(Frame(), T0.AddSeconds(6));
        Assert.True(reader.Read(Frame(ControllerButtons.South), T0.AddSeconds(7)).Accept);
    }

    [Fact]
    public void AcceptAndBackAreIndependent()
    {
        var reader = new NavIntentReader();
        NavIntent intent = reader.Read(Frame(ControllerButtons.South | ControllerButtons.East), T0);

        Assert.True(intent.Accept);
        Assert.True(intent.Back);
    }

    // ---- reset ---------------------------------------------------------------------------------

    [Fact]
    public void Reset_SwallowsTheInFlightPress()
    {
        // Handing the pad to a stream or a modal and taking it back: the buttons held at that moment must not
        // read as a fresh press when control returns, or releasing the combo that opened something immediately
        // triggers whatever the same button means underneath it.
        var reader = new NavIntentReader();
        ControllerStateFrame held = Frame(ControllerButtons.South);

        reader.Reset(held);

        Assert.False(reader.Read(held, T0).Accept);
        Assert.True(reader.Read(Frame(), T0.AddMilliseconds(16)).IsEmpty);
        Assert.True(reader.Read(held, T0.AddMilliseconds(32)).Accept);
    }

    [Fact]
    public void Reset_ForgetsAHeldDirection()
    {
        // Otherwise the direction still held on return counts as "the same direction", and the first movement
        // waits for a repeat interval that started before the interruption.
        var reader = new NavIntentReader();
        ControllerStateFrame down = Frame(ControllerButtons.DPadDown);

        reader.Read(down, T0);
        reader.Reset(down);

        Assert.Equal(NavDirection.Down, reader.Read(down, T0.AddMilliseconds(16)).Direction);
    }

    [Fact]
    public void Reset_ToZeroWouldHaveBeenWrong_WhichIsWhyItTakesAFrame()
    {
        // Pinning the reason Reset takes the current frame: clearing to "nothing held" would make the next
        // frame, with those buttons still physically down, look like a brand-new press.
        var reader = new NavIntentReader();
        reader.Reset(Frame(ControllerButtons.South));

        Assert.False(reader.Read(Frame(ControllerButtons.South), T0).Accept);
    }
}
