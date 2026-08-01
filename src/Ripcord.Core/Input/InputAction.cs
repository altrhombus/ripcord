namespace Ripcord.Core.Input;

/// <summary>
/// Something a binding can produce. This is deliberately NOT <see cref="ControllerButtons"/>: a keyboard has no
/// analogue sticks, so half of these are axis <em>directions</em> that a key press drives to full deflection.
/// Modelling axes as directions is what lets one binding table serve both a keyboard (discrete) and a remapped
/// gamepad (already analogue).
/// </summary>
public enum InputAction
{
    None = 0,

    // Face, shoulder, stick-click, and system buttons. Named by position rather than glyph (South, not Cross)
    // because the same physical position means Cross on a DualSense and A on an Xbox pad, and the protocol
    // backend owns that translation.
    South,
    East,
    West,
    North,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    LeftShoulder,
    RightShoulder,
    LeftStickClick,
    RightStickClick,
    Start,
    Select,
    Guide,
    TouchpadClick,

    // Axis directions. Each drives one half of one axis to full deflection while held; opposing bindings held
    // together cancel, which is what makes a keyboard feel like a stick rather than a latch.
    LeftStickUp,
    LeftStickDown,
    LeftStickLeft,
    LeftStickRight,
    RightStickUp,
    RightStickDown,
    RightStickLeft,
    RightStickRight,

    // Analogue triggers. A key press means fully pressed; a real trigger keeps its analogue value.
    LeftTrigger,
    RightTrigger,
}

/// <summary>Helpers for classifying an <see cref="InputAction"/> without a switch at every call site.</summary>
public static class InputActions
{
    /// <summary>The button this action maps to, or <see cref="ControllerButtons.None"/> if it drives an axis.</summary>
    public static ControllerButtons ToButton(this InputAction action) => action switch
    {
        InputAction.South => ControllerButtons.South,
        InputAction.East => ControllerButtons.East,
        InputAction.West => ControllerButtons.West,
        InputAction.North => ControllerButtons.North,
        InputAction.DPadUp => ControllerButtons.DPadUp,
        InputAction.DPadDown => ControllerButtons.DPadDown,
        InputAction.DPadLeft => ControllerButtons.DPadLeft,
        InputAction.DPadRight => ControllerButtons.DPadRight,
        InputAction.LeftShoulder => ControllerButtons.LeftShoulder,
        InputAction.RightShoulder => ControllerButtons.RightShoulder,
        InputAction.LeftStickClick => ControllerButtons.LeftStick,
        InputAction.RightStickClick => ControllerButtons.RightStick,
        InputAction.Start => ControllerButtons.Start,
        InputAction.Select => ControllerButtons.Select,
        InputAction.Guide => ControllerButtons.Guide,
        InputAction.TouchpadClick => ControllerButtons.TouchpadClick,
        _ => ControllerButtons.None,
    };

    /// <summary>Whether this action drives an axis (stick direction or trigger) rather than a button.</summary>
    public static bool IsAxis(this InputAction action) => action >= InputAction.LeftStickUp;

    /// <summary>A short, stable label for the settings UI and for persisted bindings.</summary>
    public static string DisplayName(this InputAction action) => action switch
    {
        InputAction.None => "Unbound",
        InputAction.South => "Cross / A",
        InputAction.East => "Circle / B",
        InputAction.West => "Square / X",
        InputAction.North => "Triangle / Y",
        InputAction.DPadUp => "D-pad up",
        InputAction.DPadDown => "D-pad down",
        InputAction.DPadLeft => "D-pad left",
        InputAction.DPadRight => "D-pad right",
        InputAction.LeftShoulder => "L1 / LB",
        InputAction.RightShoulder => "R1 / RB",
        InputAction.LeftStickClick => "L3 (stick click)",
        InputAction.RightStickClick => "R3 (stick click)",
        InputAction.Start => "Options / Menu",
        InputAction.Select => "Create / View",
        InputAction.Guide => "PS / Guide",
        InputAction.TouchpadClick => "Touchpad click",
        InputAction.LeftStickUp => "Left stick up",
        InputAction.LeftStickDown => "Left stick down",
        InputAction.LeftStickLeft => "Left stick left",
        InputAction.LeftStickRight => "Left stick right",
        InputAction.RightStickUp => "Right stick up",
        InputAction.RightStickDown => "Right stick down",
        InputAction.RightStickLeft => "Right stick left",
        InputAction.RightStickRight => "Right stick right",
        InputAction.LeftTrigger => "L2 / LT",
        InputAction.RightTrigger => "R2 / RT",
        _ => action.ToString(),
    };
}
