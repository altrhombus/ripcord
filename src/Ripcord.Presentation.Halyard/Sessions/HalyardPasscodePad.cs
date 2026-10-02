using Ripcord.Core.Input;

namespace Ripcord.Presentation.Halyard.Sessions;

/// <summary>What one press on the pad means to the login passcode prompt.</summary>
public enum PasscodePadAction
{
    /// <summary>A digit, in <see cref="PasscodePadInput.Digit"/>.</summary>
    Digit,

    /// <summary>East: remove the last digit, or leave the prompt when there are none.</summary>
    Back,

    /// <summary>South: sign in with what has been entered.</summary>
    Submit,
}

/// <summary>One press, read. <see cref="Digit"/> is meaningful only for <see cref="PasscodePadAction.Digit"/>.</summary>
public readonly record struct PasscodePadInput(PasscodePadAction Action, char Digit = '\0');

/// <summary>Which trigger, for a key that is one. Triggers are analog, so they are not in <see cref="ControllerButtons"/>.</summary>
public enum PasscodeTrigger
{
    None,
    Left,
    Right,
}

/// <summary>
/// One digit and the control that types it. The reader and the prompt's legend read the same table, so what the
/// prompt says a button does is what it does.
/// </summary>
/// <param name="Digit">The digit.</param>
/// <param name="Button">The button, or <see cref="ControllerButtons.None"/> for a trigger.</param>
/// <param name="Trigger">The trigger, or <see cref="PasscodeTrigger.None"/> for a button.</param>
/// <param name="VendorName">What the console's own pad calls it.</param>
/// <param name="GenericName">What a GameInput pad calls it.</param>
public sealed record PasscodeKey(
    char Digit, ControllerButtons Button, PasscodeTrigger Trigger, string VendorName, string GenericName)
{
    /// <summary>The control's name on the pad in hand.</summary>
    public string NameFor(PadFamily family) => family == PadFamily.Vendor ? VendorName : GenericName;
}

/// <summary>
/// The console's own passcode buttons, so the login passcode can be entered from the pad in the hand.
///
/// <para>
/// <b>The mapping is the console's, not ours.</b> The console's own passcode entry reads its buttons as digits,
/// and someone who knows their passcode knows it as those presses: 1 to 4 are the d-pad left, up, right and down;
/// 5 and 6 are R1 and R2; 7 and 8 are L1 and L2; 9 is triangle and 0 is square. Observed on the console itself,
/// 2026-10-02. That leaves cross and circle free, which take the prompt's own two actions, as they do everywhere
/// else in the app.
/// </para>
///
/// <para>
/// <b>Edges, and nothing already held.</b> Frames are absolute state, so a digit is a rising edge, never a held
/// button. What was held when the prompt opened is the baseline, given to the constructor, rather than a press:
/// the pad that opened the prompt may still have cross down, and reading that as Submit would close the prompt the
/// moment it appeared. It is handed in rather than taken from the first frame read because a source may publish
/// only on change, and then the first frame is itself a press. The triggers are analog, so they press past one
/// threshold and release below a lower one, and a trigger resting near the line cannot type a run of digits.
/// </para>
///
/// <para>
/// Not thread-safe: one reader per prompt, fed from the one thread that polls the pad.
/// </para>
/// </summary>
public sealed class HalyardPasscodePad
{
    /// <summary>How far a trigger travels before it counts as pressed.</summary>
    public const float TriggerPress = 0.5f;

    /// <summary>How far back it comes before it counts as released.</summary>
    public const float TriggerRelease = 0.3f;

    /// <summary>
    /// The keys, 1 to 9 and then 0: the order the console's own table lists them in, and the order the prompt's
    /// legend shows them. The d-pad is drawn as arrows on either pad; the face buttons take the names the hint
    /// bar already uses (<see cref="ButtonLabels.BadgeText"/>).
    /// </summary>
    public static IReadOnlyList<PasscodeKey> Keys { get; } =
    [
        new('1', ControllerButtons.DPadLeft, PasscodeTrigger.None, "←", "←"),
        new('2', ControllerButtons.DPadUp, PasscodeTrigger.None, "↑", "↑"),
        new('3', ControllerButtons.DPadRight, PasscodeTrigger.None, "→", "→"),
        new('4', ControllerButtons.DPadDown, PasscodeTrigger.None, "↓", "↓"),
        new('5', ControllerButtons.RightShoulder, PasscodeTrigger.None, "R1", "RB"),
        new('6', ControllerButtons.None, PasscodeTrigger.Right, "R2", "RT"),
        new('7', ControllerButtons.LeftShoulder, PasscodeTrigger.None, "L1", "LB"),
        new('8', ControllerButtons.None, PasscodeTrigger.Left, "L2", "LT"),
        Face('9', ControllerButtons.North, PromptButton.North),
        Face('0', ControllerButtons.West, PromptButton.West),
    ];

    private ControllerButtons _held;
    private bool _leftTriggerDown;
    private bool _rightTriggerDown;

    /// <summary>A reader whose baseline is <paramref name="held"/>, the pad's state when the prompt opened; null for none.</summary>
    public HalyardPasscodePad(ControllerStateFrame? held = null)
    {
        if (held is { } frame)
        {
            _held = frame.Buttons;
            _leftTriggerDown = frame.LeftTrigger >= TriggerPress;
            _rightTriggerDown = frame.RightTrigger >= TriggerPress;
        }
    }

    /// <summary>The presses in <paramref name="frame"/>, in a fixed order. Empty when nothing new was pressed.</summary>
    public IReadOnlyList<PasscodePadInput> Read(ControllerStateFrame frame)
    {
        bool rightTrigger = Trigger(frame.RightTrigger, _rightTriggerDown);
        bool leftTrigger = Trigger(frame.LeftTrigger, _leftTriggerDown);

        ControllerButtons pressed = frame.Buttons & ~_held;
        bool rightTriggerPressed = rightTrigger && !_rightTriggerDown;
        bool leftTriggerPressed = leftTrigger && !_leftTriggerDown;

        _held = frame.Buttons;
        _rightTriggerDown = rightTrigger;
        _leftTriggerDown = leftTrigger;

        List<PasscodePadInput>? inputs = null;

        foreach (PasscodeKey key in Keys)
        {
            bool down = key.Trigger switch
            {
                PasscodeTrigger.Left => leftTriggerPressed,
                PasscodeTrigger.Right => rightTriggerPressed,
                _ => (pressed & key.Button) != 0,
            };

            if (down)
            {
                (inputs ??= []).Add(new PasscodePadInput(PasscodePadAction.Digit, key.Digit));
            }
        }

        if ((pressed & ControllerButtons.East) != 0)
        {
            (inputs ??= []).Add(new PasscodePadInput(PasscodePadAction.Back));
        }

        if ((pressed & ControllerButtons.South) != 0)
        {
            (inputs ??= []).Add(new PasscodePadInput(PasscodePadAction.Submit));
        }

        return inputs ?? (IReadOnlyList<PasscodePadInput>)[];
    }

    private static PasscodeKey Face(char digit, ControllerButtons button, PromptButton face)
        => new(digit, button, PasscodeTrigger.None,
            ButtonLabels.BadgeText(face, PadFamily.Vendor), ButtonLabels.BadgeText(face, PadFamily.Generic));

    private static bool Trigger(float value, bool wasDown)
        => wasDown ? value > TriggerRelease : value >= TriggerPress;
}
