using Ripcord.Core.Input;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The mode hysteresis. The asymmetry is the whole design, so it is what these pin.
/// </summary>
public class InputModeTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static InputModeTracker Tracker(InputMode initial = InputMode.Pointer)
        => new(initial, switchAwayDwell: TimeSpan.FromMilliseconds(700));

    [Fact]
    public void SwitchingToControllerIsImmediate()
    {
        // Making someone wait for the prompts they just asked for by pressing a button is exactly backwards.
        InputModeTracker tracker = Tracker();

        tracker.ReportControllerActivity(T0);

        Assert.Equal(InputMode.Controller, tracker.Mode);
    }

    [Fact]
    public void SwitchingAwayFromControllerNeedsTheDwell()
    {
        // The couch case: a pad knocked on the sofa, or a mouse bumped while reaching for a drink, must not
        // strip the prompts off a session someone is playing with a controller.
        InputModeTracker tracker = Tracker(InputMode.Controller);

        tracker.ReportPointerActivity(T0);
        Assert.Equal(InputMode.Controller, tracker.Mode);

        tracker.ReportPointerActivity(T0.AddMilliseconds(400));
        Assert.Equal(InputMode.Controller, tracker.Mode);

        tracker.ReportPointerActivity(T0.AddMilliseconds(700));
        Assert.Equal(InputMode.Pointer, tracker.Mode);
    }

    [Fact]
    public void AControllerSignalDuringTheDwellCancelsIt()
    {
        // Evidence the user is still on the pad. Without this, a single stray click would eventually win as
        // long as another one happened to land after the dwell had elapsed.
        InputModeTracker tracker = Tracker(InputMode.Controller);

        tracker.ReportPointerActivity(T0);
        tracker.ReportControllerActivity(T0.AddMilliseconds(300));
        tracker.ReportPointerActivity(T0.AddMilliseconds(800));

        Assert.Equal(InputMode.Controller, tracker.Mode);
    }

    [Fact]
    public void ADifferentSignalDuringTheDwellRestartsIt()
    {
        // Two different desk signals are not cumulative evidence for either one.
        InputModeTracker tracker = Tracker(InputMode.Controller);

        tracker.ReportPointerActivity(T0);
        tracker.ReportKeyboardActivity(T0.AddMilliseconds(600));
        Assert.Equal(InputMode.Controller, tracker.Mode);

        tracker.ReportKeyboardActivity(T0.AddMilliseconds(1000));
        Assert.Equal(InputMode.Controller, tracker.Mode);

        tracker.ReportKeyboardActivity(T0.AddMilliseconds(1300));
        Assert.Equal(InputMode.Keyboard, tracker.Mode);
    }

    [Fact]
    public void SwitchingBetweenDeskModesIsImmediate()
    {
        // The dwell exists to protect a couch session. Between keyboard, pointer and touch a knock costs
        // nothing and waiting would just feel unresponsive.
        InputModeTracker tracker = Tracker(InputMode.Pointer);

        tracker.ReportKeyboardActivity(T0);
        Assert.Equal(InputMode.Keyboard, tracker.Mode);

        tracker.ReportTouchActivity(T0.AddMilliseconds(10));
        Assert.Equal(InputMode.Touch, tracker.Mode);

        tracker.ReportPointerActivity(T0.AddMilliseconds(20));
        Assert.Equal(InputMode.Pointer, tracker.Mode);
    }

    [Fact]
    public void ModeChanged_FiresOnlyOnRealChanges()
    {
        // A hint bar redraws on this. Firing per signal rather than per change would rebuild it on every
        // keystroke.
        InputModeTracker tracker = Tracker();
        List<InputMode> observed = [];
        tracker.ModeChanged += observed.Add;

        tracker.ReportPointerActivity(T0);
        tracker.ReportPointerActivity(T0.AddMilliseconds(10));
        tracker.ReportControllerActivity(T0.AddMilliseconds(20));
        tracker.ReportControllerActivity(T0.AddMilliseconds(30));

        Assert.Equal([InputMode.Controller], observed);
    }

    [Fact]
    public void RepeatedControllerActivityDoesNotStrandAPendingSwitch()
    {
        // Regression shape: if a pending switch survived a return to the current mode, the app could flip to
        // Pointer long after the click that requested it, with the pad in active use in between.
        InputModeTracker tracker = Tracker(InputMode.Controller);

        tracker.ReportPointerActivity(T0);
        tracker.ReportControllerActivity(T0.AddMilliseconds(100));

        for (int ms = 200; ms <= 2000; ms += 100)
        {
            tracker.ReportControllerActivity(T0.AddMilliseconds(ms));
        }

        Assert.Equal(InputMode.Controller, tracker.Mode);
    }
}
