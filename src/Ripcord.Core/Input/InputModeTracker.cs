namespace Ripcord.Core.Input;

/// <summary>How the person is driving the app right now.</summary>
public enum InputMode
{
    /// <summary>Mouse, trackpad or pen. The desk case, and the startup assumption.</summary>
    Pointer,

    /// <summary>A gamepad. The couch case: prompts stay on screen and focus visuals matter at distance.</summary>
    Controller,

    /// <summary>Physical keys. Directional model matches the pad's; prompts only for non-obvious keys.</summary>
    Keyboard,

    /// <summary>Direct touch. Every action already corresponds to a visible control, so prompts are noise.</summary>
    Touch,
}

/// <summary>
/// Decides which input mode the app is in, and refuses to be jumpy about it.
///
/// <para>
/// The interface adapts to this — an on-screen hint bar showing button prompts, focus visuals sized to be
/// legible from a sofa. That adaptation is only worth having if it is stable, which is the entire reason this
/// is a class with a rule rather than a field assigned by whichever handler fired last.
/// </para>
///
/// <para>
/// <b>The rule is deliberately asymmetric.</b> Switching <em>to</em> Controller is immediate: picking up a pad
/// is unambiguous, and making someone wait for prompts they just asked for by pressing a button is exactly
/// backwards. Switching <em>away</em> from Controller needs a deliberate act — a click, a key, a touch, never
/// mere pointer movement — and then a dwell before it takes effect. A pad nudged on the sofa, a cat on the
/// desk, or a mouse knocked while reaching for a drink must not strip the prompts off a couch session.
/// </para>
///
/// <para>
/// Pointer <em>movement</em> never switches modes at all. It is the single most easily triggered signal on a
/// desktop and the least intentional — a window appearing under a stationary cursor generates it.
/// </para>
///
/// <para>
/// Pure and clock-injected, following <see cref="ExitGestureDetector"/> and <see cref="NavIntentReader"/>: the
/// dwell is testable rather than something to sit and watch.
/// </para>
/// </summary>
public sealed class InputModeTracker
{
    /// <summary>
    /// How long a non-controller signal must persist before it takes the mode away from Controller. Long
    /// enough to ride out a knock, short enough that someone genuinely reaching for the mouse is not left
    /// looking at prompts they cannot use.
    /// </summary>
    public static readonly TimeSpan DefaultSwitchAwayDwell = TimeSpan.FromMilliseconds(700);

    private readonly TimeSpan _dwell;

    private InputMode _pending;
    private DateTimeOffset? _pendingSince;

    public InputModeTracker(InputMode initial = InputMode.Pointer, TimeSpan? switchAwayDwell = null)
    {
        Mode = initial;
        _pending = initial;
        _dwell = switchAwayDwell ?? DefaultSwitchAwayDwell;
    }

    /// <summary>The current mode. Only ever changes inside a <c>Report*</c> call, never on its own.</summary>
    public InputMode Mode { get; private set; }

    /// <summary>Raised when <see cref="Mode"/> actually changes, so a hint bar can redraw once rather than poll.</summary>
    public event Action<InputMode>? ModeChanged;

    /// <summary>
    /// A gamepad did something deliberate — a button, or a stick past its deadzone. Takes effect at once.
    /// </summary>
    public void ReportControllerActivity(DateTimeOffset now) => Apply(InputMode.Controller, now);

    /// <summary>A key was pressed. Deliberate, so it starts the dwell.</summary>
    public void ReportKeyboardActivity(DateTimeOffset now) => Apply(InputMode.Keyboard, now);

    /// <summary>
    /// A pointer button was pressed, or the wheel turned. Deliberate — unlike movement, which is not reported
    /// here at all and by design cannot change the mode.
    /// </summary>
    public void ReportPointerActivity(DateTimeOffset now) => Apply(InputMode.Pointer, now);

    /// <summary>A touch contact began.</summary>
    public void ReportTouchActivity(DateTimeOffset now) => Apply(InputMode.Touch, now);

    private void Apply(InputMode signal, DateTimeOffset now)
    {
        // Already there: nothing to decide, but clear any pending switch — a signal for the current mode is
        // evidence the user is still in it, so a half-elapsed dwell toward something else should not survive.
        if (signal == Mode)
        {
            _pending = Mode;
            _pendingSince = null;
            return;
        }

        // Picking up a pad is unambiguous. No dwell, no debate.
        if (signal == InputMode.Controller)
        {
            _pending = InputMode.Controller;
            _pendingSince = null;
            Set(InputMode.Controller);
            return;
        }

        // Leaving Controller is the case the dwell exists for. Everything else is a switch between desk-shaped
        // modes, where a knock costs nothing and waiting would just feel unresponsive.
        if (Mode != InputMode.Controller)
        {
            Set(signal);
            return;
        }

        if (signal != _pending || _pendingSince is null)
        {
            _pending = signal;
            _pendingSince = now;
            return;
        }

        if (now - _pendingSince >= _dwell)
        {
            _pendingSince = null;
            Set(signal);
        }
    }

    private void Set(InputMode mode)
    {
        if (mode == Mode)
        {
            return;
        }

        Mode = mode;
        ModeChanged?.Invoke(mode);
    }
}
