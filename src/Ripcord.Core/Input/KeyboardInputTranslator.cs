namespace Ripcord.Core.Input;

/// <summary>
/// Turns held keys into a <see cref="ControllerStateFrame"/>.
///
/// <para>
/// Deliberately a pure state machine over "which keys are down": key <em>events</em> repeat, arrive out of order
/// with modifiers, and stop arriving entirely when the window loses focus, so deriving each frame from the held
/// set rather than from event deltas is what keeps a stuck key from becoming a stuck stick. <see cref="Clear"/>
/// exists for exactly that focus-loss case.
/// </para>
/// </summary>
public sealed class KeyboardInputTranslator
{
    private readonly HashSet<int> _held = [];
    private InputBindings _bindings;

    public KeyboardInputTranslator(InputBindings? bindings = null)
        => _bindings = bindings ?? new InputBindings();

    /// <summary>Whether any bound key is currently held — i.e. whether this source has anything to contribute.</summary>
    public bool HasInput { get; private set; }

    public InputBindings Bindings
    {
        get => _bindings;
        set
        {
            _bindings = value;
            // Re-derive rather than keep stale state: a key held while its binding changed would otherwise
            // continue driving the action it used to.
            HasInput = _held.Any(k => _bindings.Keyboard.ContainsKey(k));
        }
    }

    /// <summary>Records a key as held. Returns true if this changed anything (so the caller can skip a frame).</summary>
    public bool KeyDown(int key)
    {
        if (!_bindings.Keyboard.ContainsKey(key))
        {
            return false;
        }

        bool changed = _held.Add(key);
        if (changed)
        {
            HasInput = true;
        }

        return changed;
    }

    /// <summary>Records a key as released. Returns true if this changed anything.</summary>
    public bool KeyUp(int key)
    {
        bool changed = _held.Remove(key);
        if (changed)
        {
            HasInput = _held.Count > 0;
        }

        return changed;
    }

    /// <summary>
    /// Releases everything. Call on focus loss: the OS stops delivering key-up once the window is deactivated, so
    /// without this a key held at the moment of alt-tab stays held forever — full stick deflection into a game
    /// that the user cannot correct without reconnecting.
    /// </summary>
    public void Clear()
    {
        _held.Clear();
        HasInput = false;
    }

    /// <summary>Build the frame for the current held set.</summary>
    public ControllerStateFrame ToFrame(long timestampTicks)
    {
        ControllerButtons buttons = ControllerButtons.None;
        float leftX = 0, leftY = 0, rightX = 0, rightY = 0, leftTrigger = 0, rightTrigger = 0;

        foreach (int key in _held)
        {
            if (!_bindings.Keyboard.TryGetValue(key, out InputAction action))
            {
                continue;
            }

            switch (action)
            {
                // Opposing directions ADD rather than override, so holding left and right cancels to centre.
                // Overriding would make whichever key the set happened to enumerate last win, which reads as the
                // stick sticking at full deflection.
                case InputAction.LeftStickUp: leftY += 1f; break;
                case InputAction.LeftStickDown: leftY -= 1f; break;
                case InputAction.LeftStickLeft: leftX -= 1f; break;
                case InputAction.LeftStickRight: leftX += 1f; break;
                case InputAction.RightStickUp: rightY += 1f; break;
                case InputAction.RightStickDown: rightY -= 1f; break;
                case InputAction.RightStickLeft: rightX -= 1f; break;
                case InputAction.RightStickRight: rightX += 1f; break;
                case InputAction.LeftTrigger: leftTrigger = 1f; break;
                case InputAction.RightTrigger: rightTrigger = 1f; break;
                default: buttons |= action.ToButton(); break;
            }
        }

        // Normalise diagonals. Two keys held give (1,1), which is magnitude 1.41 — pushed through a stick that
        // clamps per-axis it means diagonal movement is faster than cardinal, the classic keyboard-driving
        // artefact. Scaling to the unit circle makes a diagonal exactly as fast as a straight line.
        (leftX, leftY) = ClampToUnitCircle(leftX, leftY);
        (rightX, rightY) = ClampToUnitCircle(rightX, rightY);

        return new ControllerStateFrame(
            timestampTicks,
            buttons,
            leftX,
            leftY,
            rightX,
            rightY,
            leftTrigger,
            rightTrigger,
            Gyro: null,
            Accel: null,
            Touchpad: null);
    }

    private static (float X, float Y) ClampToUnitCircle(float x, float y)
    {
        float magnitude = MathF.Sqrt((x * x) + (y * y));
        return magnitude > 1f ? (x / magnitude, y / magnitude) : (x, y);
    }
}
