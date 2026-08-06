namespace Ripcord.Core.Input;

/// <summary>Which way the user asked to move.</summary>
public enum NavDirection
{
    None,
    Up,
    Down,
    Left,
    Right,
}

/// <summary>
/// What one controller frame asked the interface to do.
/// </summary>
/// <param name="Direction">
/// A movement to perform now — either a fresh press or an auto-repeat tick. <see cref="NavDirection.None"/>
/// while the stick is centred, and also on the frames <em>between</em> repeats, which is the point: a held
/// direction produces movement at the repeat cadence rather than on every polled frame.
/// </param>
/// <param name="Accept">The South button was pressed this frame — activate whatever has focus.</param>
/// <param name="Back">The East button was pressed this frame.</param>
/// <param name="Context">
/// The North button was pressed — open the focused item's context menu. A pad has no right-click and no menu
/// key, so without this the secondary actions on a console card are reachable only by aiming a pointer at a
/// 32-pixel overflow button.
/// </param>
/// <param name="Scroll">
/// Right-stick deflection, -1..1, positive meaning "scroll up". Analog and continuous rather than a discrete
/// step, and deliberately NOT subject to auto-repeat: a scroll that ticked at the repeat cadence would feel
/// like a ratchet where the hardware is offering a rate.
/// </param>
public readonly record struct NavIntent(
    NavDirection Direction,
    bool Accept,
    bool Back,
    bool Context = false,
    float Scroll = 0)
{
    /// <summary>Nothing to do this frame.</summary>
    public static NavIntent None { get; }

    /// <summary>True when this frame asks for anything at all, so a caller can return early.</summary>
    public bool IsEmpty
        => Direction == NavDirection.None && !Accept && !Back && !Context && Scroll == 0;
}

/// <summary>
/// Turns a stream of absolute controller frames into discrete navigation intents.
///
/// <para>
/// Extracted from <c>MainWindow</c>, where it was interleaved with focus movement and WinUI automation calls
/// and could only be exercised by holding a real pad. Everything here is arithmetic over a frame and a
/// timestamp, and every rule in it was written for a specific complaint:
/// </para>
///
/// <list type="bullet">
///   <item>
///     <b>Auto-repeat.</b> Without it, holding a stick or D-pad moved focus exactly once, so navigating a long
///     list meant flicking repeatedly — the single most obviously-wrong thing about gamepad navigation. The
///     initial pause stops an intended single step becoming two.
///   </item>
///   <item>
///     <b>Edge detection on buttons.</b> Frames are absolute state, so a held button is present in every frame;
///     acting on presence rather than on the rising edge would fire continuously for as long as it is held.
///   </item>
///   <item>
///     <b>A deadzone the user owns.</b> Sticks rest off-centre by different amounts on different pads, and a
///     resting drift that clears the threshold walks focus down a list on its own.
///   </item>
/// </list>
///
/// <para>
/// Pure and clock-injected, following <see cref="ExitGestureDetector"/>: the caller supplies <c>now</c>, so
/// repeat timing is unit-testable rather than something to try on a real pad and hope.
/// </para>
/// </summary>
public sealed class NavIntentReader
{
    /// <summary>
    /// How long a direction must be held before it starts repeating. Long enough that an intended single step
    /// does not become two.
    /// </summary>
    public static readonly TimeSpan DefaultRepeatDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>Cadence once repeating has started.</summary>
    public static readonly TimeSpan DefaultRepeatInterval = TimeSpan.FromMilliseconds(120);

    private readonly TimeSpan _repeatDelay;
    private readonly TimeSpan _repeatInterval;

    private ControllerButtons _previousButtons = ControllerButtons.None;
    private NavDirection _heldDirection = NavDirection.None;
    private DateTimeOffset _directionHeldSince;
    private DateTimeOffset _lastRepeat;

    public NavIntentReader(TimeSpan? repeatDelay = null, TimeSpan? repeatInterval = null)
    {
        _repeatDelay = repeatDelay ?? DefaultRepeatDelay;
        _repeatInterval = repeatInterval ?? DefaultRepeatInterval;
    }

    /// <summary>
    /// How far the stick must travel before it counts as a direction. Settable because it is a user setting,
    /// and it can change while the app runs.
    /// </summary>
    public double StickDeadzone { get; set; } = 0.5;

    /// <summary>
    /// Read one frame.
    /// </summary>
    /// <param name="now">
    /// The caller's clock. Supplied rather than read here so repeat timing can be tested; a caller polling a
    /// real pad passes <see cref="DateTimeOffset.UtcNow"/>.
    /// </param>
    public NavIntent Read(in ControllerStateFrame frame, DateTimeOffset now)
    {
        NavIntent intent = new(
            ReadDirection(frame, now),
            Accept: IsRisingEdge(frame.Buttons, ControllerButtons.South),
            Back: IsRisingEdge(frame.Buttons, ControllerButtons.East),
            Context: IsRisingEdge(frame.Buttons, ControllerButtons.North),
            Scroll: ScrollOf(frame));

        _previousButtons = frame.Buttons;
        return intent;
    }

    /// <summary>
    /// Forget what is currently held, without emitting anything.
    ///
    /// <para>
    /// For handing the pad to someone else and taking it back — a stream capturing input, or a modal opening.
    /// The buttons held at that moment must not read as a rising edge when control returns, or releasing the
    /// combo that opened a thing immediately triggers whatever the same button means underneath it. Takes the
    /// frame rather than clearing to zero for exactly that reason: clearing would make the <em>next</em> frame,
    /// with those buttons still down, look like a fresh press.
    /// </para>
    /// </summary>
    public void Reset(in ControllerStateFrame frame)
    {
        _previousButtons = frame.Buttons;
        _heldDirection = NavDirection.None;
    }

    private NavDirection ReadDirection(in ControllerStateFrame frame, DateTimeOffset now)
    {
        NavDirection direction = DirectionOf(frame);

        if (direction == NavDirection.None)
        {
            _heldDirection = NavDirection.None;
            return NavDirection.None;
        }

        // A new direction moves immediately and starts the clock.
        if (direction != _heldDirection)
        {
            _heldDirection = direction;
            _directionHeldSince = now;
            _lastRepeat = now;
            return direction;
        }

        // Still held: silent until the initial delay has passed, then one movement per interval.
        if (now - _directionHeldSince < _repeatDelay || now - _lastRepeat < _repeatInterval)
        {
            return NavDirection.None;
        }

        _lastRepeat = now;
        return direction;
    }

    /// <summary>
    /// D-pad or stick, whichever is asking. Checked in a fixed order rather than by magnitude, so a diagonal
    /// resolves the same way every time instead of oscillating between two axes near 45°.
    /// </summary>
    private NavDirection DirectionOf(in ControllerStateFrame frame)
    {
        float deadzone = (float)StickDeadzone;

        if ((frame.Buttons & ControllerButtons.DPadUp) != 0 || frame.LeftStickY > deadzone)
        {
            return NavDirection.Up;
        }

        if ((frame.Buttons & ControllerButtons.DPadDown) != 0 || frame.LeftStickY < -deadzone)
        {
            return NavDirection.Down;
        }

        if ((frame.Buttons & ControllerButtons.DPadLeft) != 0 || frame.LeftStickX < -deadzone)
        {
            return NavDirection.Left;
        }

        if ((frame.Buttons & ControllerButtons.DPadRight) != 0 || frame.LeftStickX > deadzone)
        {
            return NavDirection.Right;
        }

        return NavDirection.None;
    }

    /// <summary>
    /// Right-stick deflection past the deadzone, passed through at full resolution so a caller can scroll at a
    /// rate rather than in steps. Zero inside the deadzone, so a resting stick never scrolls.
    /// </summary>
    private float ScrollOf(in ControllerStateFrame frame)
        => Math.Abs(frame.RightStickY) > (float)StickDeadzone ? frame.RightStickY : 0;

    private bool IsRisingEdge(ControllerButtons current, ControllerButtons button)
        => (current & button) != 0 && (_previousButtons & button) == 0;
}
