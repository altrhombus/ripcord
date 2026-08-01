using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Ripcord.Core.Input;
using Windows.System;

namespace Ripcord_App.Dialogs;

/// <summary>One row of the rebinding list. A plain class because x:Bind needs a named type, not a tuple.</summary>
public sealed class KeyBindingRow(InputAction action, string actionName, string keyNames)
{
    public InputAction Action { get; } = action;

    public string ActionName { get; } = actionName;

    public string KeyNames { get; } = keyNames;
}

/// <summary>
/// Rebinds keys by capturing a keypress.
///
/// <para>
/// Capture rather than a dropdown of key names: it is faster, it needs no exhaustive key list, and it binds keys
/// nobody thought to offer — which is the point of having a remap at all. While capture is armed every keypress is
/// swallowed, so the dialog's own Escape-to-close is suspended; that is deliberate, since Escape is a key someone
/// may reasonably want to look at, and the InfoBar says what is happening.
/// </para>
/// </summary>
public sealed partial class KeyBindingsDialog : ContentDialog
{
    /// <summary>
    /// Actions offered for rebinding, in the order a person would look for them. Axis directions are included
    /// because on a keyboard they ARE the sticks; a gamepad user never sees this dialog.
    /// </summary>
    private static readonly InputAction[] Rebindable =
    [
        InputAction.LeftStickUp, InputAction.LeftStickDown, InputAction.LeftStickLeft, InputAction.LeftStickRight,
        InputAction.RightStickUp, InputAction.RightStickDown, InputAction.RightStickLeft, InputAction.RightStickRight,
        InputAction.South, InputAction.East, InputAction.West, InputAction.North,
        InputAction.DPadUp, InputAction.DPadDown, InputAction.DPadLeft, InputAction.DPadRight,
        InputAction.LeftShoulder, InputAction.RightShoulder,
        InputAction.LeftTrigger, InputAction.RightTrigger,
        InputAction.LeftStickClick, InputAction.RightStickClick,
        InputAction.Start, InputAction.Select, InputAction.Guide, InputAction.TouchpadClick,
    ];

    private readonly ObservableCollection<KeyBindingRow> _rows = [];
    private InputAction _capturing = InputAction.None;

    public KeyBindingsDialog(InputBindings bindings)
    {
        InitializeComponent();
        Result = bindings;
        BindingList.ItemsSource = _rows;
        Refresh();

        PrimaryButtonClick += (_, args) =>
        {
            // Keep the dialog open after a reset so the result is visible.
            args.Cancel = true;
            Result = Result with { Keyboard = InputBindings.DefaultKeyboard };
            Refresh();
        };

        // Capture has to see the key before anything else consumes it, hence PreviewKeyDown on the dialog.
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// The edited bindings. Read after the dialog closes.
    ///
    /// <para>
    /// Named Result rather than Bindings because the XAML compiler generates its own <c>Bindings</c> member on any
    /// partial class that uses <c>x:Bind</c>, and a property of that name collides with it (CS0102). Result also
    /// matches PairConsoleDialog.
    /// </para>
    /// </summary>
    public InputBindings Result { get; private set; }

    private void Rebind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InputAction action })
        {
            return;
        }

        _capturing = action;
        CaptureBar.Title = $"Press a key for {action.DisplayName()}";
        CaptureBar.Message = "Backspace clears it. Escape, F3 and F11 are reserved by the app.";
        CaptureBar.IsOpen = true;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_capturing == InputAction.None)
        {
            return;
        }

        e.Handled = true;
        int key = (int)e.Key;

        // Backspace unbinds. Consequence, accepted deliberately: Backspace cannot itself be bound through this
        // dialog, since the clear gesture has to win. Core still permits it, so a config file may bind it.
        if (e.Key == VirtualKey.Back)
        {
            Result = Result.WithoutAction(_capturing);
            Done("Cleared.");
            return;
        }

        if (!InputBindings.IsBindable(key))
        {
            // Reserved or unrecognised: say so and stay armed, rather than silently doing nothing.
            CaptureBar.Message = $"{KeyName(key)} is reserved by the app. Press a different key.";
            CaptureBar.Severity = InfoBarSeverity.Warning;
            return;
        }

        // One key drives one action, so binding a key already in use moves it. Warn, because the previous action
        // silently losing its key is exactly the kind of thing a user discovers mid-game.
        string? stolenFrom = Result.Keyboard.TryGetValue(key, out InputAction previous) && previous != _capturing
            ? previous.DisplayName()
            : null;

        Result = Result.WithoutAction(_capturing).WithKey(key, _capturing);
        Done(stolenFrom is null ? null : $"{KeyName(key)} was {stolenFrom}; it is now {_capturing.DisplayName()}.");
    }

    private void Done(string? message)
    {
        _capturing = InputAction.None;
        Refresh();

        if (message is null)
        {
            CaptureBar.IsOpen = false;
            CaptureBar.Severity = InfoBarSeverity.Informational;
            return;
        }

        CaptureBar.Title = message;
        CaptureBar.Message = string.Empty;
        CaptureBar.Severity = InfoBarSeverity.Informational;
        CaptureBar.IsOpen = true;
    }

    private void Refresh()
    {
        _rows.Clear();
        foreach (InputAction action in Rebindable)
        {
            IReadOnlyList<int> keys = Result.KeysFor(action);
            string names = keys.Count == 0 ? "—" : string.Join(", ", keys.Select(KeyName));
            _rows.Add(new KeyBindingRow(action, action.DisplayName(), names));
        }
    }

    /// <summary>
    /// A readable name for a virtual-key code. The VirtualKey enum's own names are mostly right but a handful read
    /// badly to a user ("Number1" for the 1 key), so those are spelled out.
    /// </summary>
    private static string KeyName(int key) => (VirtualKey)key switch
    {
        VirtualKey.Space => "Space",
        VirtualKey.Enter => "Enter",
        VirtualKey.Tab => "Tab",
        VirtualKey.Back => "Backspace",
        VirtualKey.Escape => "Escape",
        VirtualKey.Left => "Left arrow",
        VirtualKey.Right => "Right arrow",
        VirtualKey.Up => "Up arrow",
        VirtualKey.Down => "Down arrow",
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (key - (int)VirtualKey.Number0))).ToString(),
        >= VirtualKey.A and <= VirtualKey.Z => ((char)key).ToString(),
        var other => other.ToString(),
    };
}
