using Ripcord.Core.Settings;

namespace Ripcord.Core.Input;

/// <summary>
/// Recognises the "leave the stream" controller gesture.
///
/// <para>
/// This exists because a running stream had no controller exit at all: app-chrome navigation is deliberately
/// suppressed while input is being forwarded (otherwise B would navigate back instead of reaching the console),
/// so on a handheld with no keyboard or mouse the only ways out were the navigation pane — which needs
/// touch — or killing the process.
/// </para>
///
/// <para>
/// The default gesture is Start + Select + L1 + R1 held together, following the convention other streaming
/// clients use. Two properties make it the right choice: it is effectively impossible to hit accidentally
/// mid-game, and it avoids the PS/Guide button, which <em>must</em> keep reaching the console so the player can
/// open the console's own menu. A hold requirement guards against a fluke simultaneous press.
/// </para>
///
/// <para>Pure and clock-injected, so the timing is unit-testable rather than something to try on a real pad.</para>
/// </summary>
public sealed class ExitGestureDetector(ExitGesture gesture, TimeSpan? holdDuration = null)
{
    /// <summary>Long enough to rule out an accident, short enough not to feel unresponsive.</summary>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromMilliseconds(600);

    private readonly ExitGesture _gesture = gesture;
    private readonly TimeSpan _hold = holdDuration ?? DefaultHold;

    private DateTimeOffset? _heldSince;
    private bool _fired;

    /// <summary>Progress toward triggering, 0..1. Lets the UI show the hold filling rather than nothing.</summary>
    public double Progress { get; private set; }

    /// <summary>
    /// Feed one controller frame. Returns true exactly once per completed gesture; the buttons must be released
    /// and re-pressed to trigger again (so holding it does not fire repeatedly on the way out).
    /// </summary>
    public bool Update(in ControllerStateFrame frame, DateTimeOffset now)
    {
        if (!IsComboHeld(frame.Buttons))
        {
            _heldSince = null;
            _fired = false;
            Progress = 0;
            return false;
        }

        _heldSince ??= now;

        TimeSpan held = now - _heldSince.Value;
        Progress = _hold > TimeSpan.Zero ? Math.Clamp(held / _hold, 0, 1) : 1;

        if (_fired || held < _hold)
        {
            return false;
        }

        _fired = true;
        Progress = 1;
        return true;
    }

    /// <summary>Forget any in-progress hold — call when the stream ends or focus is lost.</summary>
    public void Reset()
    {
        _heldSince = null;
        _fired = false;
        Progress = 0;
    }

    private bool IsComboHeld(ControllerButtons buttons) => _gesture switch
    {
        ExitGesture.StartSelectShoulders =>
            buttons.HasFlag(ControllerButtons.Start)
            && buttons.HasFlag(ControllerButtons.Select)
            && buttons.HasFlag(ControllerButtons.LeftShoulder)
            && buttons.HasFlag(ControllerButtons.RightShoulder),

        ExitGesture.BothSticksClicked =>
            buttons.HasFlag(ControllerButtons.LeftStick)
            && buttons.HasFlag(ControllerButtons.RightStick),

        _ => false,
    };

    /// <summary>Short player-facing description, for the on-screen hint and the settings page.</summary>
    public static string Describe(ExitGesture gesture) => Describe(gesture, PadFamily.Vendor);

    /// <summary>
    /// The same, in the names on <paramref name="family"/>'s pad. The stream's hint is read with a pad in hand, and
    /// "Options + Create + L1 + R1" names nothing on an Xbox pad (2026-10-02).
    /// </summary>
    public static string Describe(ExitGesture gesture, PadFamily family)
        => Buttons(gesture, family) is { } buttons ? $"Hold {buttons}" : "Press Esc";

    /// <summary>
    /// Just the buttons, "L3 + R3", in <paramref name="family"/>'s names; null for <see cref="ExitGesture.None"/>.
    /// For a sentence that names both pads, as the settings page does.
    /// </summary>
    public static string? Buttons(ExitGesture gesture, PadFamily family) => (gesture, family) switch
    {
        (ExitGesture.StartSelectShoulders, PadFamily.Vendor) => "Options + Create + L1 + R1",
        (ExitGesture.StartSelectShoulders, _) => "Menu + View + LB + RB",
        (ExitGesture.BothSticksClicked, PadFamily.Vendor) => "L3 + R3",
        (ExitGesture.BothSticksClicked, _) => "LS + RS",
        _ => null,
    };
}
