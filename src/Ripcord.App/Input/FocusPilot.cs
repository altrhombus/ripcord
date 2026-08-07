using Windows.Foundation;
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
using Ripcord_App.Services;

namespace Ripcord_App.Input;

/// <summary>
/// Drives focus from navigation intents: where focus goes, what activating does, what the right stick
/// scrolls, and what North opens.
///
/// <para>
/// Lifted out of <c>MainWindow</c>, which had accumulated the whole of it — directional search, range
/// adjustment, automation-peer activation and popup-root resolution — alongside navigation, the title bar and
/// the stream layer. None of it is about being a window; all of it is about a visual tree, which is why it
/// takes the root as a parameter and can therefore be pointed at a dialog later without changing.
/// </para>
///
/// <para>
/// <b>Why WinUI's own engine and not a hand-rolled one.</b> WinUI 3 desktop gives the full XY-focus engine —
/// <c>FindNextElement</c>, strategies, overrides — but <em>no gamepad input source at all</em>: with no
/// CoreWindow, gamepad virtual keys never arrive in <c>KeyDown</c>. So reading the pad has to be ours
/// permanently, while deciding which element is next must not be. Both the pad and the keyboard call the same
/// engine with the same options, so a pad Down and a keyboard Down land on the same element by construction.
/// </para>
///
/// <para>
/// <b>And never by synthesizing key events.</b> A pad→arrow bridge would appear to get XY navigation free, and
/// would also inject arrows into the console stream and into text boxes.
/// </para>
/// </summary>
public sealed class FocusPilot(
    Func<FrameworkElement?> contentRoot,
    Action seedFocus,
    Func<Control, bool>? openTextEntry = null)
{
    public void MoveFocus(NavDirection direction)
    {
        var winrtDirection = direction switch
        {
            NavDirection.Up => FocusNavigationDirection.Up,
            NavDirection.Down => FocusNavigationDirection.Down,
            NavDirection.Left => FocusNavigationDirection.Left,
            NavDirection.Right => FocusNavigationDirection.Right,
            _ => FocusNavigationDirection.None,
        };

        if (winrtDirection == FocusNavigationDirection.None || DirectionalRoot() is not { } searchRoot)
        {
            return;
        }

        // Left/Right on a range control adjusts it rather than leaving it, which is what the arrow keys
        // already do — so a pad needs no separate "engagement" mode with its own visual state and its own way
        // to get stuck. Up/Down still moves focus, so the control is never a trap.
        if ((direction == NavDirection.Left || direction == NavDirection.Right)
            && TryAdjustRange(searchRoot.XamlRoot, increase: direction == NavDirection.Right))
        {
            return;
        }

        // The simple TryMoveFocus(direction) overload throws COMException 0x8000FFFF in a WinUI Desktop app (as
        // opposed to UWP, where a single implicit CoreWindow root makes it valid) — a desktop app can host
        // multiple windows, so FindNextElementOptions.SearchRoot must say which visual tree to search.
        var options = new FindNextElementOptions { SearchRoot = searchRoot };

        if (FocusManager.FindNextElement(winrtDirection, options) is UIElement candidate)
        {
            // FocusState.Keyboard, never Programmatic: a Programmatic focus change does not draw the focus
            // visual, so directional navigation would move an invisible caret.
            candidate.Focus(FocusState.Keyboard);

            // A candidate below the fold is useless if the list does not scroll to it.
            candidate.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = AppMotion.Enabled });
            return;
        }

        // Nothing in that direction. Usually that is correct and focus should stay put — pressing against the
        // end of a row should not teleport the caret somewhere else.
        //
        // A range control is the exception, and the history here is worth keeping because it was a
        // misdiagnosis rather than a hard problem. Up/Down would not leave the bitrate slider, and that was
        // read as WinUI's focus engagement — a state defined in gamepad key events that never arrive in a
        // desktop app, so it looked un-exitable by construction. It was not that at all: DirectionalRoot was
        // handing the search a TOOLTIP as its root (see NavigablePopups), so the search found nothing from
        // anywhere, and the slider was merely where it was noticed. Two fixes were written against the wrong
        // cause and neither worked, which in hindsight was the evidence.
        //
        // What remains after that is genuine but small, and is handled below.
        object? focused = FocusManager.GetFocusedElement(searchRoot.XamlRoot);

        if (focused is null)
        {
            seedFocus();
            return;
        }

        bool vertical = direction is NavDirection.Up or NavDirection.Down;
        if (!vertical || focused is not FrameworkElement element || !IsRangeControl(element))
        {
            return;
        }

        // Search again FROM THE ROW, not from the control.
        //
        // A Slider sits in a layout of its own inside a settings row — a horizontal panel holding the slider
        // and its value readout. Directional search starts from the focused element's rectangle, and from
        // inside that panel the rows above and below do not project onto it cleanly, so the search finds
        // nothing and focus stays put. HintRect is the supported way to say "search as if you were here":
        // point it at the whole row and the neighbouring rows are found normally.
        //
        // This replaces a tab-order fallback that moved focus by a different engine entirely. It did get the
        // user out, but tab order and layout order are not the same thing, so it SKIPPED the next row — which
        // is how a workaround announces that it is not the fix.
        if (RowOf(element) is { } row)
        {
            var fromRow = new FindNextElementOptions
            {
                SearchRoot = searchRoot,
                HintRect = BoundsIn(row, searchRoot),
                ExclusionRect = BoundsIn(element, searchRoot),
            };

            if (FocusManager.FindNextElement(winrtDirection, fromRow) is UIElement neighbour)
            {
                neighbour.Focus(FocusState.Keyboard);
                neighbour.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = AppMotion.Enabled });
            }
        }
    }


    /// <summary>
    /// Nudge the focused range control (a Slider) one step. Returns false when focus is not on one, so the
    /// caller falls through to ordinary directional movement.
    /// </summary>
    private static bool TryAdjustRange(XamlRoot? xamlRoot, bool increase)
    {
        if (xamlRoot is null || FocusManager.GetFocusedElement(xamlRoot) is not FrameworkElement focused)
        {
            return false;
        }

        var peer = FrameworkElementAutomationPeer.FromElement(focused)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(focused);

        if (peer?.GetPattern(PatternInterface.RangeValue) is not IRangeValueProvider range || range.IsReadOnly)
        {
            return false;
        }

        // SmallChange is the Slider's StepFrequency, so a pad step matches an arrow-key step exactly.
        double step = range.SmallChange > 0 ? range.SmallChange : 1;
        double target = Math.Clamp(range.Value + (increase ? step : -step), range.Minimum, range.Maximum);

        if (Math.Abs(target - range.Value) > double.Epsilon)
        {
            range.SetValue(target);
        }

        return true;
    }


    /// <summary>
    /// The visual tree directional focus should search: the topmost open popup if there is one, otherwise the
    /// window content.
    ///
    /// <para>
    /// A <see cref="ContentDialog"/>, a <c>MenuFlyout</c> and a <c>ComboBox</c> dropdown all render in the
    /// XamlRoot's popup root, which is <b>not</b> a descendant of <c>Window.Content</c> — so a search root of
    /// the window content excludes them entirely and focus cannot move inside them.
    /// <see cref="FocusManager.GetFocusedElement(XamlRoot)"/>, which activation uses, is XamlRoot-wide and so
    /// was never affected. That asymmetry is the whole of the "directional gamepad focus cannot get inside a
    /// ContentDialog (it activates, it does not move)" behaviour this project recorded as a platform
    /// limitation: it was ours, and it was this one line.
    /// </para>
    /// </summary>
    public FrameworkElement? DirectionalRoot()
    {
        if (contentRoot() is not { } content)
        {
            return null;
        }

        if (content.XamlRoot is not { } xamlRoot)
        {
            return content;
        }

        IReadOnlyList<Popup> popups = NavigablePopups(xamlRoot);
        if (popups.Count == 0)
        {
            return content;
        }

        // "Topmost is last" is not a documented guarantee, so prefer the popup that actually holds focus —
        // which is also what keeps a soft keyboard opened over a dialog working — and only fall back to
        // scanning from the end.
        if (FocusManager.GetFocusedElement(xamlRoot) is DependencyObject focused)
        {
            for (int i = popups.Count - 1; i >= 0; i--)
            {
                if (popups[i] is { IsOpen: true, Child: FrameworkElement child } && IsInSubtree(child, focused))
                {
                    return child;
                }
            }
        }

        for (int i = popups.Count - 1; i >= 0; i--)
        {
            if (popups[i] is { IsOpen: true, Child: FrameworkElement child })
            {
                return child;
            }
        }

        return content;
    }


    private static bool IsInSubtree(DependencyObject root, DependencyObject candidate)
    {
        for (DependencyObject? node = candidate; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, root))
            {
                return true;
            }
        }

        return false;
    }


    public void ActivateFocusedElement()
    {
        // The pilot no longer owns a window, so the root comes from the same callback everything else uses.
        XamlRoot? xamlRoot = contentRoot()?.XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        if (FocusManager.GetFocusedElement(xamlRoot) is not FrameworkElement focused)
        {
            return;
        }

        // A text field first, because it is the one control where activation cannot mean "press it". A TextBox
        // exposes no Invoke pattern, so before this the accept button did nothing whatsoever on a text field —
        // and four of them stand between a user and a paired console. Handled by the host, which owns the
        // keyboard overlay; the pilot only knows that something answered.
        if (focused is Control control && IsTextEntry(control) && openTextEntry?.Invoke(control) == true)
        {
            return;
        }

        var peer = FrameworkElementAutomationPeer.FromElement(focused)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(focused);

        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invokeProvider)
        {
            invokeProvider.Invoke();
        }
        else if (peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selectionProvider)
        {
            selectionProvider.Select();
        }
        else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggleProvider)
        {
            toggleProvider.Toggle();
        }
        // A ComboBox exposes ExpandCollapse and no Invoke, so without this the accept button did nothing at
        // all on the six ComboBoxes in Settings — the page was reachable by pad but not operable by it. Once
        // the dropdown is open it becomes the topmost popup, so DirectionalRoot() lets focus move inside it.
        else if (peer?.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expandProvider)
        {
            if (expandProvider.ExpandCollapseState == ExpandCollapseState.Expanded)
            {
                expandProvider.Collapse();
            }
            else
            {
                expandProvider.Expand();
            }
        }
    }

    /// <summary>
    /// Scroll the nearest scrollable ancestor of whatever has focus, at a rate the stick sets.
    ///
    /// <para>
    /// The one genuinely new interaction rather than a re-expression of something the keyboard already does,
    /// and the one that makes a long page navigable with a pad: without it, reaching the bottom of Settings
    /// means walking focus through every control on the way down. Deliberately does NOT move focus — it moves
    /// the viewport, exactly as a mouse wheel does, so reading ahead does not disturb where you are.
    /// </para>
    /// </summary>
    /// <param name="deflection">-1..1, positive meaning up, already past the deadzone.</param>
    public void Scroll(float deflection)
    {
        if (deflection == 0 || DirectionalRoot()?.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        if (FocusManager.GetFocusedElement(xamlRoot) is not DependencyObject focused
            || NearestScrollViewer(focused) is not { } scroller)
        {
            return;
        }

        // Rate, not steps: the stick is analog and the hardware is offering a speed. Squared so a small
        // deflection nudges precisely and a full push travels — linear felt simultaneously twitchy near the
        // centre and slow at the edge.
        double rate = deflection * Math.Abs(deflection) * MaxScrollPixelsPerFrame;

        // Negated: positive deflection is up, and vertical offset grows downward.
        scroller.ChangeView(null, scroller.VerticalOffset - rate, null, disableAnimation: true);
    }

    /// <summary>
    /// Open the focused element's context menu. A pad has no right-click and no menu key, so without this the
    /// secondary actions on a console card — rename, details, remove — are reachable only by aiming a pointer
    /// at a 32-pixel overflow button.
    /// </summary>
    public void OpenContextMenu()
    {
        if (DirectionalRoot()?.XamlRoot is not { } xamlRoot
            || FocusManager.GetFocusedElement(xamlRoot) is not UIElement focused)
        {
            return;
        }

        // The nearest ancestor carrying a ContextFlyout — the same object right-click and the menu key open,
        // so there is one menu with one definition rather than a pad-shaped second path to keep in agreement.
        // WinUI exposes no way to raise ContextRequested programmatically, which is why this reaches for the
        // flyout itself rather than trying to replay the event.
        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { ContextFlyout: { } flyout } anchor)
            {
                anchor.StartBringIntoView();
                flyout.ShowAt(anchor);
                return;
            }
        }
    }

    /// <summary>
    /// How far a full stick push scrolls per frame. Tuned against the ~60 Hz poll: enough to cross a settings
    /// page in about a second, slow enough to stop on a row.
    /// </summary>
    private const double MaxScrollPixelsPerFrame = 26;

    /// <summary>The closest ancestor that can actually scroll, so nested lists behave the way the eye expects.</summary>
    private static ScrollViewer? NearestScrollViewer(DependencyObject from)
    {
        for (DependencyObject? node = from; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer { ScrollableHeight: > 0 } scroller)
            {
                return scroller;
            }
        }

        return null;
    }

    /// <summary>Diagnostic: which ancestor, if any, carries a ContextFlyout.</summary>

    /// <summary>
    /// Close the topmost open popup, if there is one.
    ///
    /// <para>
    /// Back has to mean "close this" before it means "leave this page". A pad has no Escape, so with a context
    /// menu open and Back wired straight to frame navigation, the only pressable dismissal was gone — the menu
    /// stayed up and Back appeared dead. Returns true when it handled the press, so the caller knows not to
    /// navigate.
    /// </para>
    /// </summary>
    public bool TryDismissPopup()
    {
        if (contentRoot()?.XamlRoot is not { } xamlRoot)
        {
            return false;
        }

        IReadOnlyList<Popup> popups = NavigablePopups(xamlRoot);

        for (int i = popups.Count - 1; i >= 0; i--)
        {
            popups[i].IsOpen = false;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A control where typing is the point. Named types rather than "exposes a writable Value pattern",
    /// because a ComboBox exposes one too and opening a keyboard over a list of fixed choices would be wrong.
    /// </summary>
    private static bool IsTextEntry(Control control)
        => control is TextBox or PasswordBox or RichEditBox;

    /// <summary>
    /// The open popups that are actually places a user can be — the ones holding something focusable.
    ///
    /// <para>
    /// <b>Found on hardware, and it made the pad look dead.</b> A TOOLTIP is a popup. So is the dimmed smoke
    /// layer behind a ContentDialog. Neither contains anything focusable, and both are frequently the topmost
    /// popup on the XamlRoot. Treating them as the search root meant directional focus searched a tooltip,
    /// found nothing, and did nothing — so hovering the mouse over a button with a tooltip silently killed
    /// controller navigation until the tooltip faded. Reported as "sometimes the controller stops responding
    /// and there's a tooltip on screen", which is exactly what it was.
    /// </para>
    ///
    /// <para>
    /// The same list drives dismissal, and the same bug was there in a second costume: Back closed the smoke
    /// layer rather than the dialog, so the first press removed the dimming and left the prompt sitting there.
    /// One filter fixes both, because both are the same mistake — assuming every popup is a surface.
    /// </para>
    ///
    /// <para>
    /// "Holds something focusable" rather than a type check on ToolTip: it is the property actually being
    /// relied on, it needs no list of popup types to keep current, and any future decoration rendered in a
    /// popup is excluded without anyone having to remember to add it.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Popup> NavigablePopups(XamlRoot xamlRoot)
    {
        List<Popup> navigable = [];

        foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
        {
            if (popup is { IsOpen: true, Child: DependencyObject child }
                && FocusManager.FindFirstFocusableElement(child) is not null)
            {
                navigable.Add(popup);
            }
        }

        return navigable;
    }

    /// <summary>
    /// The row a control belongs to: the nearest ancestor materially wider than the control itself, which is
    /// what "the thing above" and "the thing below" are actually neighbours of. Bounded rather than walking to
    /// the page root, since the page root is a neighbour of nothing.
    /// </summary>
    private static FrameworkElement? RowOf(FrameworkElement element)
    {
        DependencyObject? node = VisualTreeHelper.GetParent(element);

        for (int depth = 0; depth < 6 && node is not null; depth++, node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement candidate
                && candidate.ActualWidth > element.ActualWidth + 1
                && candidate.ActualHeight > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static Rect BoundsIn(FrameworkElement element, UIElement reference)
    {
        try
        {
            return element.TransformToVisual(reference)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (Exception)
        {
            // TransformToVisual throws when the two are not in the same tree, which a mid-teardown race can
            // produce. An empty rect simply means the retry finds nothing, which is the pre-existing behaviour.
            return default;
        }
    }

    /// <summary>A control whose own directional behaviour will not release focus vertically — in practice a Slider.</summary>
    private static bool IsRangeControl(object? element)
    {
        if (element is not FrameworkElement fe)
        {
            return false;
        }

        var peer = FrameworkElementAutomationPeer.FromElement(fe)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(fe);

        return peer?.GetPattern(PatternInterface.RangeValue) is IRangeValueProvider;
    }

    /// <summary>
    /// Whether focus is somewhere a pad can actually navigate from.
    ///
    /// <para>
    /// Null is the obvious case, but not the only one. Clicking empty space in the window leaves focus on a
    /// CONTAINER — a ScrollViewer, a panel, the page itself — and a container that encloses every candidate has
    /// nothing above, below or beside it, so directional search finds nothing in any direction. To a user that
    /// is indistinguishable from the pad being dead, which is exactly how it was reported.
    /// </para>
    /// </summary>
    public bool NeedsFocusSeed()
    {
        if (contentRoot()?.XamlRoot is not { } xamlRoot)
        {
            return false;
        }

        object? focused = FocusManager.GetFocusedElement(xamlRoot);

        return focused switch
        {
            null => true,
            ScrollViewer => true,
            Control { IsTabStop: false } => true,
            not Control => true,
            _ => false,
        };
    }
}
