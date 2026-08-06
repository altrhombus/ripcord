using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Ripcord.Core.Input;
using Ripcord.Presentation.Settings;
using Windows.System;

namespace Ripcord_App.Pages;

/// <summary>
/// One row of the rebinding list.
///
/// <para>
/// Notifying, and that is the point rather than an implementation detail: the row's keys change on every
/// rebind, and the alternative — rebuilding the collection — destroys the item containers, which destroys
/// focus. Focus at that moment is on the Change button of the row just edited, so a wholesale refresh would
/// throw the user back to the top of the page after every single binding. This is the exception the app's
/// state rule already carves out for collections, for exactly this reason.
/// </para>
/// </summary>
public sealed class KeyBindingRow(InputAction action, string actionName, string keyNames) : INotifyPropertyChanged
{
    private string _keyNames = keyNames;

    public event PropertyChangedEventHandler? PropertyChanged;

    public InputAction Action { get; } = action;

    public string ActionName { get; } = actionName;

    public string KeyNames
    {
        get => _keyNames;
        set
        {
            if (_keyNames == value)
            {
                return;
            }

            _keyNames = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyNames)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangeButtonName)));
        }
    }

    /// <summary>
    /// What a screen reader says for the row's button. Twenty-six identical "Change" buttons are useless
    /// without it, and the visible label cannot carry the action name without making every row shout.
    /// </summary>
    public string ChangeButtonName => $"Change the key for {ActionName}. Currently {KeyNames}.";
}

/// <summary>
/// Rebinds keys by capturing a keypress.
///
/// <para>
/// Capture rather than a dropdown of key names: it is faster, needs no exhaustive key list, and binds keys
/// nobody thought to offer — which is the point of having a remap at all.
/// </para>
///
/// <para>
/// <b>Why this is a page and was a dialog.</b> A <c>ContentDialog</c> reserves Escape (close) and Enter (the
/// default button), and capture-by-keypress wants every key. Enter is a <em>default binding here</em> — Start —
/// so the conflict was not hypothetical, and the consequence was worse than awkward: the dialog's default
/// button was "Reset to defaults", so a stray Enter while capture was not armed silently wiped every binding
/// the user had. Capture worked only by swallowing keys before the host saw them, which is a workaround for
/// being in the wrong container. A page reserves nothing.
/// </para>
///
/// <para>
/// Changes save as they are made, as everywhere else in Settings. A page has no OK button, and giving it one
/// would be a dialog's habit surviving the move.
/// </para>
/// </summary>
public sealed partial class KeyBindingsPage : Page
{
    /// <summary>
    /// Actions offered for rebinding, in the order a person would look for them. Axis directions are included
    /// because on a keyboard they ARE the sticks.
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
    private readonly SettingsViewModel _viewModel;

    private InputBindings _bindings;
    private InputAction _capturing = InputAction.None;

    public KeyBindingsPage()
    {
        InitializeComponent();

        _viewModel = App.Services.CreateSettingsViewModel();
        _bindings = App.Services.Settings.Current.InputBindings;

        BindingList.ItemsSource = _rows;
        Refresh();

        // Capture must see the key before anything else does. On a page nothing else wants these keys, but
        // arming still has to beat the list's own arrow-key navigation, so the preview stage is still right.
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void Rebind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InputAction action })
        {
            return;
        }

        _capturing = action;
        CaptureBar.Title = $"Press a key for {action.DisplayName()}";
        CaptureBar.Message = "Backspace clears it. Escape, F3 and F11 are reserved by the app.";
        CaptureBar.Severity = InfoBarSeverity.Informational;
        CaptureBar.IsOpen = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _bindings = _bindings with { Keyboard = InputBindings.DefaultKeyboard };
        Commit();
        Refresh();
        Announce("Reset to the default keys.");
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_capturing == InputAction.None)
        {
            return;
        }

        e.Handled = true;
        int key = (int)e.Key;

        // Backspace unbinds. Consequence, accepted deliberately: Backspace cannot itself be bound here, since
        // the clear gesture has to win. Core still permits it, so a config file may bind it.
        if (e.Key == VirtualKey.Back)
        {
            _bindings = _bindings.WithoutAction(_capturing);
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

        // One key drives one action, so binding a key already in use moves it. Warn, because the previous
        // action silently losing its key is exactly the kind of thing a user discovers mid-game.
        string? stolenFrom = _bindings.Keyboard.TryGetValue(key, out InputAction previous) && previous != _capturing
            ? previous.DisplayName()
            : null;

        _bindings = _bindings.WithoutAction(_capturing).WithKey(key, _capturing);
        Done(stolenFrom is null ? null : $"{KeyName(key)} was {stolenFrom}; it is now {_capturing.DisplayName()}.");
    }

    private void Done(string? message)
    {
        _capturing = InputAction.None;
        Commit();
        Refresh();

        if (message is null)
        {
            CaptureBar.IsOpen = false;
            CaptureBar.Severity = InfoBarSeverity.Informational;
            return;
        }

        Announce(message);
    }

    private void Announce(string message)
    {
        CaptureBar.Title = message;
        CaptureBar.Message = string.Empty;
        CaptureBar.Severity = InfoBarSeverity.Informational;
        CaptureBar.IsOpen = true;
    }

    /// <summary>
    /// Persist. Every edit, immediately — the page has no OK button, so an unsaved edit would be one the user
    /// has no way to commit and every reason to think already applied.
    /// </summary>
    private void Commit() => _viewModel.SetInputBindings(_bindings);

    /// <summary>
    /// Bring the displayed keys back in line with <see cref="_bindings"/>.
    ///
    /// <para>
    /// Rows are built once and then UPDATED, never rebuilt. Clearing the collection destroys the item
    /// containers, and focus at this moment is on the Change button of the row just edited — so a wholesale
    /// refresh would throw the user to the top of the page after every rebind, which is the whole reason
    /// KeyBindingRow notifies.
    /// </para>
    /// </summary>
    private void Refresh()
    {
        if (_rows.Count == 0)
        {
            foreach (InputAction action in Rebindable)
            {
                _rows.Add(new KeyBindingRow(action, action.DisplayName(), NamesFor(action)));
            }

            return;
        }

        foreach (KeyBindingRow row in _rows)
        {
            row.KeyNames = NamesFor(row.Action);
        }
    }

    private string NamesFor(InputAction action)
    {
        IReadOnlyList<int> keys = _bindings.KeysFor(action);
        return keys.Count == 0 ? "—" : string.Join(", ", keys.Select(KeyName));
    }

    /// <summary>
    /// A readable name for a virtual-key code. The VirtualKey enum's own names are mostly right but a handful
    /// read badly to a user ("Number1" for the 1 key), so those are spelled out.
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
