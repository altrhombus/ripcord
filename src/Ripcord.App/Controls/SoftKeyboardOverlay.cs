using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Ripcord.Core.Input;
using Ripcord_App.Input;

namespace Ripcord_App.Controls;

/// <summary>
/// Text entry for someone holding a controller.
///
/// <para>
/// <b>The hole this fills.</b> Four text fields stand between a user and a paired console — link code, account
/// id, host address, name — and a pad could operate none of them. Pressing Select on a text box did nothing at
/// all, because a <c>TextBox</c> exposes no Invoke pattern, so the pairing flow simply could not be completed
/// without reaching for a keyboard. On a handheld there isn't one.
/// </para>
///
/// <para>
/// <b>Why a Popup, and why that is the whole trick.</b> It renders in the XamlRoot's popup root, so it appears
/// above a <c>ContentDialog</c> — which matters, since the login-pin prompt is itself a dialog and is exactly
/// where a keypad is needed. And because <see cref="FocusPilot.DirectionalRoot"/> already prefers the popup
/// holding focus, directional navigation works inside it with <em>zero</em> special-casing here. That is the
/// return on having fixed the search root properly rather than special-casing dialogs at the time.
/// </para>
///
/// <para>
/// <b>Not per-digit spinners.</b> The obvious cheap alternative — up/down on each character — is miserable for
/// an IP address and unusable for an account id, and it invents an interaction nobody has seen before. A
/// keyboard is the thing every console does, so it needs no explaining.
/// </para>
///
/// <para>
/// Writes go through <see cref="IValueProvider"/> rather than to <c>TextBox.Text</c>, so the validation the
/// pairing flow already hangs off <c>TextChanged</c> keeps working untouched, and so a future non-TextBox
/// target needs no change here. Keys are ≥56×56, which makes this the touch keyboard on a handheld as well —
/// one control rather than two, and the mode that gets less testing is the one that would have rotted.
/// </para>
/// </summary>
public sealed class SoftKeyboardOverlay
{
    private const double KeySize = 56;
    private const double KeyGap = 8;

    private Popup? _popup;
    private ShellInputScope? _scope;
    private readonly List<Button> _letterKeys = [];
    private bool _shifted;

    public bool IsOpen => _popup is { IsOpen: true };

    /// <summary>
    /// Put the keyboard up for <paramref name="target"/>. Returns whether it went up.
    ///
    /// <para>
    /// <b>Synchronous, and that is a correction.</b> This was <c>Task&lt;bool&gt; ShowAsync</c> completing on
    /// dismissal, and every caller discarded the task — so a failure to open, including a thrown exception,
    /// vanished into an unobserved Task while the caller still reported the press as handled. The button did
    /// nothing and said nothing. Opening a popup was never asynchronous work; only waiting for it to close
    /// was, and nobody was waiting.
    /// </para>
    ///
    /// <para>
    /// False means there was nothing to do — no XamlRoot, a target exposing no writable value, or a keyboard
    /// already up. The caller should treat the press as unhandled and let it fall through.
    /// </para>
    /// </summary>
    public bool TryOpen(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (IsOpen || target.XamlRoot is not { } root)
        {
            return false;
        }

        if (ValueProviderOf(target) is not { IsReadOnly: false } value)
        {
            return false;
        }

        _shifted = false;
        _letterKeys.Clear();

        FrameworkElement surface = BuildSurface(root, target, value);

        _popup = new Popup
        {
            XamlRoot = root,

            // Light dismiss would close on the first press that misses a key, which on a pad is easy to do and
            // impossible to understand. Back closes it, via FocusPilot's popup dismissal.
            IsLightDismissEnabled = false,
            Child = surface,
        };

        _popup.Closed += OnPopupClosed;
        root.Changed += OnXamlRootChanged;

        // The keyboard owns the pad while it is up, and the scope underneath remembers the text box it was on
        // — so dismissing returns focus to the field being edited without this having to arrange it.
        _scope = new ShellInputScope(InputScopeKind.Modal, focusRoot: () => target.XamlRoot);
        App.Input.Scopes.Push(_scope);

        _popup.IsOpen = true;

        // After a layout pass: the keys do not exist as focusable elements until the popup has been measured.
        surface.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (IsOpen && FocusManager.FindFirstFocusableElement(surface) is Control first)
                {
                    first.Focus(FocusState.Keyboard);
                }
            });

        return true;
    }

    /// <summary>Close it, if it is up. Safe to call when it is not.</summary>
    public void Hide()
    {
        if (_popup is { IsOpen: true } popup)
        {
            popup.IsOpen = false;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Layout
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Which keyboard a field wants, taken from the <c>InputScope</c> it already declares.
    ///
    /// <para>
    /// <c>LoginPinDialog</c> declares <c>NumericPin</c> today, purely as a hint to the OS touch keyboard, so it
    /// selects the keypad here with no change to that file. A field that wants a keypad should say so the same
    /// way rather than by being named in this class.
    /// </para>
    /// </summary>
    private static bool WantsKeypad(Control target)
    {
        InputScope? scope = target switch
        {
            TextBox box => box.InputScope,
            PasswordBox box => box.InputScope,
            _ => null,
        };

        if (scope?.Names is not { Count: > 0 } names)
        {
            return false;
        }

        foreach (InputScopeName name in names)
        {
            if (name.NameValue is InputScopeNameValue.NumericPin
                or InputScopeNameValue.Number
                or InputScopeNameValue.TelephoneNumber
                or InputScopeNameValue.Digits)
            {
                return true;
            }
        }

        return false;
    }

    private FrameworkElement BuildSurface(XamlRoot root, Control target, IValueProvider value)
    {
        var keys = new StackPanel { Spacing = KeyGap, HorizontalAlignment = HorizontalAlignment.Center };

        foreach (string[] row in WantsKeypad(target) ? KeypadRows : QwertyRows)
        {
            keys.Children.Add(BuildRow(row, value));
        }

        var card = new Border
        {
            Background = Brush("AcrylicInAppFillColorDefaultBrush", "SolidBackgroundFillColorBaseBrush"),
            BorderBrush = Brush("SurfaceStrokeColorDefaultBrush", "ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16, 16, 16, 32),
            Child = keys,
        };

        // A full-window host rather than an offset popup: the keyboard then centres itself by ordinary layout,
        // and the scrim says the rest of the window is not listening — which is true, because a Modal scope is
        // on the stack.
        var host = new Grid
        {
            Width = root.Size.Width,
            Height = root.Size.Height,
            Background = Brush("SmokeFillColorDefaultBrush", "ControlFillColorDefaultBrush"),
        };

        host.Children.Add(card);
        return host;
    }

    private StackPanel BuildRow(string[] row, IValueProvider value)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = KeyGap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        foreach (string key in row)
        {
            panel.Children.Add(BuildKey(key, value));
        }

        return panel;
    }

    private Button BuildKey(string key, IValueProvider value)
    {
        var button = new Button
        {
            MinWidth = KeySize,
            MinHeight = KeySize,
            Content = KeyFace(key),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        // Space earns its width; everything else is square so the grid reads as a keyboard.
        if (key == KeySpace)
        {
            button.MinWidth = KeySize * 5;
        }

        AutomationProperties.SetName(button, KeyName(key));

        if (key.Length == 1 && char.IsLetter(key[0]))
        {
            _letterKeys.Add(button);
        }

        button.Click += (_, _) => OnKey(key, value);
        return button;
    }

    // ----------------------------------------------------------------------------------------------------
    // Keys
    // ----------------------------------------------------------------------------------------------------

    private const string KeyBackspace = "";
    private const string KeyShift = "↑";
    private const string KeySpace = " ";
    private const string KeyDone = "✓";

    private static readonly string[][] KeypadRows =
    [
        ["1", "2", "3"],
        ["4", "5", "6"],
        ["7", "8", "9"],
        [KeyBackspace, "0", KeyDone],
    ];

    /// <summary>
    /// Compact rather than complete: enough for an IP address, a link code and an account id, which is the
    /// whole of what this app asks anyone to type. A full keyboard would not fit at 56px keys on a handheld.
    /// </summary>
    private static readonly string[][] QwertyRows =
    [
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"],
        ["q", "w", "e", "r", "t", "y", "u", "i", "o", "p"],
        ["a", "s", "d", "f", "g", "h", "j", "k", "l", "-"],
        ["z", "x", "c", "v", "b", "n", "m", ".", "_", "@"],
        [KeyShift, KeySpace, KeyBackspace, KeyDone],
    ];

    private void OnKey(string key, IValueProvider value)
    {
        switch (key)
        {
            case KeyDone:
                Hide();
                return;

            case KeyShift:
                _shifted = !_shifted;
                foreach (Button letter in _letterKeys)
                {
                    if (letter.Content is string face && face.Length == 1)
                    {
                        letter.Content = _shifted ? face.ToUpperInvariant() : face.ToLowerInvariant();
                    }
                }

                return;

            case KeyBackspace:
            {
                string current = value.Value ?? string.Empty;
                if (current.Length > 0)
                {
                    Write(value, current[..^1]);
                }

                return;
            }

            default:
            {
                string typed = _shifted && key.Length == 1 ? key.ToUpperInvariant() : key;
                Write(value, (value.Value ?? string.Empty) + typed);

                // Shift is one-shot, as it is on every phone keyboard: holding a modifier down with a thumb
                // while aiming at a key with the same thumb is not possible.
                if (_shifted)
                {
                    OnKey(KeyShift, value);
                }

                return;
            }
        }
    }

    private static void Write(IValueProvider value, string text)
    {
        try
        {
            value.SetValue(text);
        }
        catch (Exception ex)
        {
            // A target can reject a value (MaxLength, a validating setter). Refusing a keystroke is the correct
            // outcome and must not take the keyboard — or the process — down with it.
            System.Diagnostics.Debug.WriteLine($"Soft keyboard rejected input: {ex.Message}");
        }
    }

    private static string KeyFace(string key) => key switch
    {
        KeyBackspace => "⌫",
        KeyShift => "⇧",
        KeySpace => "space",
        KeyDone => "Done",
        _ => key,
    };

    /// <summary>Spoken names, since a screen reader cannot read a glyph, and neither can a distant eye.</summary>
    private static string KeyName(string key) => key switch
    {
        KeyBackspace => "Backspace",
        KeyShift => "Shift",
        KeySpace => "Space",
        KeyDone => "Done",
        _ => key,
    };

    // ----------------------------------------------------------------------------------------------------
    // Lifetime
    // ----------------------------------------------------------------------------------------------------

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_popup?.Child is Grid host)
        {
            host.Width = sender.Size.Width;
            host.Height = sender.Size.Height;
        }
    }

    private void OnPopupClosed(object? sender, object e)
    {
        // Closed rather than an explicit dismiss path, so Back — which closes the topmost popup without telling
        // anyone — tears down exactly as pressing Done does. One exit, however it was reached.
        if (_popup is { } popup)
        {
            popup.Closed -= OnPopupClosed;

            if (popup.XamlRoot is { } root)
            {
                root.Changed -= OnXamlRootChanged;
            }
        }

        if (_scope is { } scope)
        {
            App.Input.Scopes.Pop(scope);
            _scope = null;
        }

        _popup = null;
        _letterKeys.Clear();
    }

    private static IValueProvider? ValueProviderOf(Control target)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(target)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(target);

        return peer?.GetPattern(PatternInterface.Value) as IValueProvider;
    }

    /// <summary>
    /// A theme brush by key, with a fallback for the one that may not exist — an acrylic resource is absent
    /// when the user has transparency off, and a null Background there would make the keyboard unreadable
    /// against whatever is behind it.
    /// </summary>
    private static Brush? Brush(string key, string fallbackKey)
    {
        ResourceDictionary resources = Application.Current.Resources;

        if (resources.TryGetValue(key, out object? found) && found is Brush brush)
        {
            return brush;
        }

        return resources.TryGetValue(fallbackKey, out object? fallback) ? fallback as Brush : null;
    }
}
