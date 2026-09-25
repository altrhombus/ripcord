using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Ripcord.Core.Input;

namespace Ripcord_App.Controls;

/// <summary>
/// The row of button prompts along the bottom of the window.
///
/// <para>
/// <b>It knows nothing about pages.</b> It draws whatever the top input scope declares, so a new surface gets
/// correct prompts by declaring them where it already declares its focus root — and "who owns the pad" and
/// "what the pad does" cannot drift apart, because they are the same object. The bar subscribes to the scope
/// stack and to the input mode, and has no other inputs.
/// </para>
///
/// <para>
/// <b>Visible in Controller mode, mostly absent otherwise</b>, which is the whole reason the mode tracker has
/// hysteresis. On a pad the prompts are the only way to discover what the buttons do. With a mouse or a finger
/// every action already corresponds to a visible control, so the bar would be a strip of noise restating it.
/// A keyboard sits between: it shows only the prompts carrying a key label, which are by definition the ones
/// nobody would guess — Enter and Escape do not need announcing.
/// </para>
///
/// <para>
/// Never a focus target. It describes what the buttons do; landing on it would be focus disappearing into
/// something that cannot be activated, which is exactly the bug the title bar had.
/// </para>
/// </summary>
public sealed class InputHintBar : ContentControl
{
    private readonly StackPanel _prompts = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 16,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private IReadOnlyList<InputPrompt> _current = [];
    private InputMode _mode = InputMode.Pointer;
    private PadFamily _family = PadFamily.Generic;
    private bool _padAttached;

    public InputHintBar()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(16, 8, 16, 8);
        Visibility = Visibility.Collapsed;

        // Announced as one line rather than as a dozen separate labels a screen reader would read on the way
        // past. The text is rebuilt with the prompts.
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);

        Content = _prompts;
    }

    /// <summary>The prompts to draw. Comes from the top scope; the bar never composes its own.</summary>
    public void Show(IReadOnlyList<InputPrompt>? prompts)
    {
        _current = prompts ?? [];
        Rebuild();
    }

    /// <summary>How the person is driving, which decides whether the bar is worth any space at all.</summary>
    public void SetMode(InputMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        Rebuild();
    }

    /// <summary>
    /// Whether a pad is attached at all, which decides whether a keyboard user is shown anything.
    ///
    /// <para>
    /// <b>Because the bar was appearing and vanishing on a desk with no controller on it.</b> In Keyboard
    /// mode it offers the one prompt a keyboard user might not know — the Menu key for a context menu — and
    /// the input mode follows whichever device was touched last. So alternating between keyboard and mouse
    /// made the bar flicker, for a hint about a key, to somebody who had no pad and never would.
    /// </para>
    ///
    /// <para>
    /// With a pad attached the prompt is worth keeping in Keyboard mode: that is somebody who has both and
    /// may be about to pick one up. Controller mode is unaffected either way — a pad in hand is a pad
    /// attached.
    /// </para>
    /// </summary>
    public void SetPadAttached(bool attached)
    {
        if (_padAttached == attached)
        {
            return;
        }

        _padAttached = attached;
        Rebuild();
    }

    /// <summary>
    /// Which pad is in hand, so the badges say what is written on it. See <see cref="ButtonLabels"/> for why
    /// this changes words and never glyphs.
    /// </summary>
    public void SetPadFamily(PadFamily family)
    {
        if (_family == family)
        {
            return;
        }

        _family = family;
        Rebuild();
    }

    private void Rebuild()
    {
        _prompts.Children.Clear();

        foreach (InputPrompt prompt in Visible())
        {
            _prompts.Children.Add(BuildPrompt(prompt));
        }

        Visibility = _prompts.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<InputPrompt> Visible()
    {
        switch (_mode)
        {
            case InputMode.Controller:
                return _current;

            // Only what a keyboard user would not already know, and only when a pad exists to make the
            // question live. A prompt earns its place by having a key label at all — see InputPrompt.KeyLabel.
            case InputMode.Keyboard:
                return _padAttached ? Where(_current, p => p.KeyLabel is not null) : [];

            // Touch and pointer: every action has a visible control, so a bar restating them is noise.
            default:
                return [];
        }
    }

    private static IEnumerable<InputPrompt> Where(IReadOnlyList<InputPrompt> source, Func<InputPrompt, bool> keep)
    {
        foreach (InputPrompt prompt in source)
        {
            if (keep(prompt))
            {
                yield return prompt;
            }
        }
    }

    /// <summary>
    /// One prompt: our own badge with text in it, then the verb.
    ///
    /// <para>
    /// The verb is not optional and there is deliberately no way to render a badge without it — a lone badge is
    /// illegible at couch distance, meaningless to anyone who has not memorised the pad, and silent to a screen
    /// reader. See <see cref="ButtonLabels"/> for the other reason.
    /// </para>
    /// </summary>
    private FrameworkElement BuildPrompt(InputPrompt prompt)
    {
        string badgeText = _mode == InputMode.Keyboard && prompt.KeyLabel is { } key
            ? key
            : ButtonLabels.BadgeText(prompt.Button, _family);

        var badge = new Border
        {
            Background = Brush("ControlFillColorSecondaryBrush"),
            BorderBrush = Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = badgeText,
                Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
            },
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(badge);
        row.Children.Add(new TextBlock
        {
            Text = prompt.Verb,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextFillColorSecondaryBrush"),
            Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
        });

        AutomationProperties.SetName(row, $"{badgeText}: {prompt.Verb}");
        return row;
    }

    private static Brush? Brush(string key)
        => Application.Current.Resources.TryGetValue(key, out object? found) ? found as Brush : null;
}
