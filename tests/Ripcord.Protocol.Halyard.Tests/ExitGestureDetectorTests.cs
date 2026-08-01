using Ripcord.Core.Input;
using Ripcord.Core.Settings;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The stream-exit gesture. This is the only way off a stream on a handheld with no keyboard, so the properties
/// that matter are: it must fire reliably when intended, must not fire on ordinary gameplay input, and must not
/// repeat while still held.
/// </summary>
public class ExitGestureDetectorTests
{
    private static DateTimeOffset T0 => new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private static ControllerStateFrame Frame(ControllerButtons buttons)
        => new(0, buttons, 0, 0, 0, 0, 0, 0, null, null, null);

    private const ControllerButtons ExitCombo =
        ControllerButtons.Start | ControllerButtons.Select
        | ControllerButtons.LeftShoulder | ControllerButtons.RightShoulder;

    [Fact]
    public void HeldForLongEnough_Fires()
    {
        var d = new ExitGestureDetector(ExitGesture.StartSelectShoulders);

        Assert.False(d.Update(Frame(ExitCombo), T0));
        Assert.True(d.Update(Frame(ExitCombo), T0 + ExitGestureDetector.DefaultHold));
    }

    [Fact]
    public void ReleasedEarly_DoesNotFire()
    {
        var d = new ExitGestureDetector(ExitGesture.StartSelectShoulders);

        d.Update(Frame(ExitCombo), T0);
        d.Update(Frame(ControllerButtons.None), T0 + TimeSpan.FromMilliseconds(200));

        // Re-pressing starts the hold over rather than resuming it.
        Assert.False(d.Update(Frame(ExitCombo), T0 + TimeSpan.FromMilliseconds(210)));
        Assert.False(d.Update(Frame(ExitCombo), T0 + TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void FiresOnlyOnceWhileStillHeld()
    {
        // Otherwise the exit would re-trigger every frame on the way out and could dismiss whatever comes next.
        var d = new ExitGestureDetector(ExitGesture.StartSelectShoulders);
        d.Update(Frame(ExitCombo), T0);

        Assert.True(d.Update(Frame(ExitCombo), T0 + ExitGestureDetector.DefaultHold));
        Assert.False(d.Update(Frame(ExitCombo), T0 + ExitGestureDetector.DefaultHold * 2));
        Assert.False(d.Update(Frame(ExitCombo), T0 + ExitGestureDetector.DefaultHold * 3));
    }

    [Fact]
    public void CanFireAgainAfterAFullReleaseAndRepress()
    {
        var d = new ExitGestureDetector(ExitGesture.StartSelectShoulders);
        d.Update(Frame(ExitCombo), T0);
        Assert.True(d.Update(Frame(ExitCombo), T0 + ExitGestureDetector.DefaultHold));

        d.Update(Frame(ControllerButtons.None), T0 + TimeSpan.FromSeconds(2));
        d.Update(Frame(ExitCombo), T0 + TimeSpan.FromSeconds(3));
        Assert.True(d.Update(Frame(ExitCombo), T0 + TimeSpan.FromSeconds(3) + ExitGestureDetector.DefaultHold));
    }

    [Theory]
    // Ordinary gameplay input that must never exit the stream.
    [InlineData(ControllerButtons.South)]
    [InlineData(ControllerButtons.Start)]
    [InlineData(ControllerButtons.Select)]
    [InlineData(ControllerButtons.Start | ControllerButtons.Select)]
    [InlineData(ControllerButtons.LeftShoulder | ControllerButtons.RightShoulder)]
    [InlineData(ControllerButtons.Start | ControllerButtons.LeftShoulder | ControllerButtons.RightShoulder)]
    [InlineData(ControllerButtons.Guide)] // must reach the console, never exit
    public void PartialOrUnrelatedInput_NeverFires(ControllerButtons buttons)
    {
        var d = new ExitGestureDetector(ExitGesture.StartSelectShoulders);

        for (int ms = 0; ms <= 3000; ms += 100)
        {
            Assert.False(d.Update(Frame(buttons), T0 + TimeSpan.FromMilliseconds(ms)));
        }
    }

    [Fact]
    public void ExtraButtonsAlongsideTheCombo_StillFire()
    {
        // The player may well be holding something else; the combo is a subset test, not an exact match.
        var d = new ExitGestureDetector(ExitGesture.StartSelectShoulders);
        ControllerButtons withExtras = ExitCombo | ControllerButtons.South | ControllerButtons.DPadUp;

        d.Update(Frame(withExtras), T0);
        Assert.True(d.Update(Frame(withExtras), T0 + ExitGestureDetector.DefaultHold));
    }

    [Fact]
    public void Progress_ReportsPartialHold()
    {
        var d = new ExitGestureDetector(ExitGesture.StartSelectShoulders);
        Assert.Equal(0, d.Progress);

        d.Update(Frame(ExitCombo), T0);
        d.Update(Frame(ExitCombo), T0 + (ExitGestureDetector.DefaultHold / 2));
        Assert.InRange(d.Progress, 0.4, 0.6);

        d.Update(Frame(ExitCombo), T0 + ExitGestureDetector.DefaultHold);
        Assert.Equal(1, d.Progress);
    }

    [Fact]
    public void Progress_ResetsOnRelease()
    {
        var d = new ExitGestureDetector(ExitGesture.StartSelectShoulders);
        d.Update(Frame(ExitCombo), T0);
        d.Update(Frame(ExitCombo), T0 + (ExitGestureDetector.DefaultHold / 2));
        d.Update(Frame(ControllerButtons.None), T0 + ExitGestureDetector.DefaultHold);

        Assert.Equal(0, d.Progress);
    }

    [Fact]
    public void BothSticksGesture_Works()
    {
        var d = new ExitGestureDetector(ExitGesture.BothSticksClicked);
        ControllerButtons combo = ControllerButtons.LeftStick | ControllerButtons.RightStick;

        d.Update(Frame(combo), T0);
        Assert.True(d.Update(Frame(combo), T0 + ExitGestureDetector.DefaultHold));

        // One stick alone is not enough.
        var single = new ExitGestureDetector(ExitGesture.BothSticksClicked);
        single.Update(Frame(ControllerButtons.LeftStick), T0);
        Assert.False(single.Update(Frame(ControllerButtons.LeftStick), T0 + ExitGestureDetector.DefaultHold));
    }

    [Fact]
    public void NoneGesture_NeverFires()
    {
        var d = new ExitGestureDetector(ExitGesture.None);
        d.Update(Frame(ExitCombo), T0);
        Assert.False(d.Update(Frame(ExitCombo), T0 + TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Reset_AbandonsAnInProgressHold()
    {
        var d = new ExitGestureDetector(ExitGesture.StartSelectShoulders);
        d.Update(Frame(ExitCombo), T0);
        d.Reset();

        Assert.Equal(0, d.Progress);
        Assert.False(d.Update(Frame(ExitCombo), T0 + ExitGestureDetector.DefaultHold));
    }

    [Fact]
    public void EveryGestureHasAPlayerFacingDescription()
    {
        foreach (ExitGesture g in Enum.GetValues<ExitGesture>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ExitGestureDetector.Describe(g)));
        }
    }
}
