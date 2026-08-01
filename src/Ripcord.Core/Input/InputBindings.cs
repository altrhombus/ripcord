using System.Collections.Frozen;

namespace Ripcord.Core.Input;

/// <summary>
/// User-configurable input mapping: which keys drive which actions, and how a gamepad's buttons are relabelled.
///
/// <para>
/// Keys are held as integer virtual-key codes rather than an enum, so Core stays free of any Windows or WinUI
/// type while still being the place the mapping lives and is tested. The UI layer supplies the codes and the
/// human-readable names.
/// </para>
///
/// <para>
/// The gamepad remap exists for hardware nobody anticipated: third-party pads and handhelds routinely report a
/// physically-South button as East, or swap shoulders, and without a remap the only fix is a code change. It is
/// identity by default, so an ordinary pad is unaffected.
/// </para>
/// </summary>
public sealed record InputBindings
{
    /// <summary>Virtual-key code to the action it triggers. Empty means "keyboard input does nothing".</summary>
    public IReadOnlyDictionary<int, InputAction> Keyboard { get; init; } = DefaultKeyboard;

    /// <summary>
    /// Physical gamepad button to the button it should be reported as. Absent entries pass through unchanged, so
    /// this only ever needs entries for the buttons a given device gets wrong.
    /// </summary>
    public IReadOnlyDictionary<ControllerButtons, ControllerButtons> GamepadRemap { get; init; }
        = FrozenDictionary<ControllerButtons, ControllerButtons>.Empty;

    /// <summary>
    /// Whether keyboard input is routed to the console at all. Off by default: a user with a pad has no reason
    /// for stray typing to reach their game, and the keys below overlap ordinary window shortcuts.
    /// </summary>
    public bool KeyboardEnabled { get; init; }

    /// <summary>
    /// Keys the session UI reserves for itself, which therefore must never be bound to a game action. Escape is
    /// the staged exit (fullscreen → windowed → leave), F11 toggles fullscreen and F3 the diagnostics overlay.
    /// Binding Escape in particular would make a fullscreen stream inescapable on a keyboard-only machine, which
    /// is the same trap the controller exit gesture exists to prevent.
    /// </summary>
    public static FrozenSet<int> ReservedKeys { get; } = new[]
    {
        VirtualKeys.Escape,
        VirtualKeys.F3,
        VirtualKeys.F11,
    }.ToFrozenSet();

    /// <summary>
    /// The default keyboard layout: WASD for the left stick, arrows for the right, TFGH for the d-pad, and the
    /// face buttons on QWER+Space where a right hand can reach them while the left drives movement.
    /// </summary>
    public static FrozenDictionary<int, InputAction> DefaultKeyboard { get; } = new Dictionary<int, InputAction>
    {
        // Left stick — movement hand.
        [VirtualKeys.W] = InputAction.LeftStickUp,
        [VirtualKeys.S] = InputAction.LeftStickDown,
        [VirtualKeys.A] = InputAction.LeftStickLeft,
        [VirtualKeys.D] = InputAction.LeftStickRight,

        // Right stick — arrows, so it works without remapping on a laptop with no numpad.
        [VirtualKeys.Up] = InputAction.RightStickUp,
        [VirtualKeys.Down] = InputAction.RightStickDown,
        [VirtualKeys.Left] = InputAction.RightStickLeft,
        [VirtualKeys.Right] = InputAction.RightStickRight,

        // D-pad, kept off the arrows so both it and the right stick are reachable at once.
        [VirtualKeys.T] = InputAction.DPadUp,
        [VirtualKeys.G] = InputAction.DPadDown,
        [VirtualKeys.F] = InputAction.DPadLeft,
        [VirtualKeys.H] = InputAction.DPadRight,

        // Face buttons.
        [VirtualKeys.Space] = InputAction.South,
        [VirtualKeys.E] = InputAction.East,
        [VirtualKeys.Q] = InputAction.West,
        [VirtualKeys.R] = InputAction.North,

        // Shoulders and triggers.
        [VirtualKeys.Number1] = InputAction.LeftShoulder,
        [VirtualKeys.Number2] = InputAction.RightShoulder,
        [VirtualKeys.Number3] = InputAction.LeftTrigger,
        [VirtualKeys.Number4] = InputAction.RightTrigger,

        // Stick clicks.
        [VirtualKeys.Z] = InputAction.LeftStickClick,
        [VirtualKeys.C] = InputAction.RightStickClick,

        // System buttons. Note Escape is NOT here — see ReservedKeys.
        [VirtualKeys.Enter] = InputAction.Start,
        [VirtualKeys.Tab] = InputAction.Select,
        [VirtualKeys.P] = InputAction.Guide,
        [VirtualKeys.V] = InputAction.TouchpadClick,
    }.ToFrozenDictionary();

    /// <summary>
    /// Whether <paramref name="key"/> may be bound. Rejects the reserved UI keys, and rejects 0 because that is
    /// what an unrecognised key arrives as and would otherwise silently bind everything unknown to one action.
    /// </summary>
    public static bool IsBindable(int key) => key != 0 && !ReservedKeys.Contains(key);

    /// <summary>
    /// The same bindings with <paramref name="key"/> assigned to <paramref name="action"/>, and that key removed
    /// from any action it previously drove.
    ///
    /// <para>
    /// One key drives at most one action, but an action may have several keys — so this clears the key's old
    /// assignment rather than the action's other keys. Rebinding a reserved key is ignored rather than throwing:
    /// the caller is a settings page reacting to a keypress, and a thrown exception there is a crash.
    /// </para>
    /// </summary>
    public InputBindings WithKey(int key, InputAction action)
    {
        if (!IsBindable(key))
        {
            return this;
        }

        var map = new Dictionary<int, InputAction>(Keyboard);
        if (action == InputAction.None)
        {
            map.Remove(key);
        }
        else
        {
            map[key] = action;
        }

        return this with { Keyboard = map };
    }

    /// <summary>The same bindings with every key for <paramref name="action"/> removed.</summary>
    public InputBindings WithoutAction(InputAction action)
    {
        var map = new Dictionary<int, InputAction>(Keyboard);
        foreach (int key in map.Where(kv => kv.Value == action).Select(kv => kv.Key).ToList())
        {
            map.Remove(key);
        }

        return this with { Keyboard = map };
    }

    /// <summary>Every key currently bound to <paramref name="action"/>, in ascending key order for stable display.</summary>
    public IReadOnlyList<int> KeysFor(InputAction action)
        => Keyboard.Where(kv => kv.Value == action).Select(kv => kv.Key).Order().ToList();

    // ---- value equality ----
    //
    // Hand-written because a record's generated equality compares MEMBERS by reference, and both members here are
    // dictionaries. Left generated, two bindings with identical contents would compare unequal — which silently
    // breaks anything asking "did the settings change?", including the settings round-trip test that caught this.

    public bool Equals(InputBindings? other)
        => other is not null
           && KeyboardEnabled == other.KeyboardEnabled
           && SameContents(Keyboard, other.Keyboard)
           && SameContents(GamepadRemap, other.GamepadRemap);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(KeyboardEnabled);

        // Ordered, so two equal bindings built in a different order hash the same.
        foreach ((int key, InputAction action) in Keyboard.OrderBy(kv => kv.Key))
        {
            hash.Add(key);
            hash.Add(action);
        }

        foreach ((ControllerButtons from, ControllerButtons to) in GamepadRemap.OrderBy(kv => kv.Key))
        {
            hash.Add(from);
            hash.Add(to);
        }

        return hash.ToHashCode();
    }

    private static bool SameContents<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> a, IReadOnlyDictionary<TKey, TValue> b)
        where TKey : notnull
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        foreach ((TKey key, TValue value) in a)
        {
            if (!b.TryGetValue(key, out TValue? other) || !EqualityComparer<TValue>.Default.Equals(value, other))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The virtual-key codes referenced by the default layout.
///
/// <para>
/// These are the Win32 VK_* values, which <c>Windows.System.VirtualKey</c> also uses, so a UI layer can pass its
/// enum straight through as an int. Defined here so Core needs no Windows reference for the mapping it owns; only
/// the codes actually used are listed, because a full VK table would be dead weight.
/// </para>
/// </summary>
public static class VirtualKeys
{
    public const int Tab = 0x09;
    public const int Enter = 0x0D;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;

    public const int Number1 = 0x31;
    public const int Number2 = 0x32;
    public const int Number3 = 0x33;
    public const int Number4 = 0x34;

    public const int A = 0x41;
    public const int C = 0x43;
    public const int D = 0x44;
    public const int E = 0x45;
    public const int F = 0x46;
    public const int G = 0x47;
    public const int H = 0x48;
    public const int P = 0x50;
    public const int Q = 0x51;
    public const int R = 0x52;
    public const int S = 0x53;
    public const int T = 0x54;
    public const int V = 0x56;
    public const int W = 0x57;
    public const int Z = 0x5A;

    public const int F3 = 0x72;
    public const int F11 = 0x7A;
}
