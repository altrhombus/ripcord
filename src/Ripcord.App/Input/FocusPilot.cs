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

        // WinUI's own search first; ours only when it finds nothing. On hardware (2026-10-01) the engine stopped
        // offering "Add another console" as a candidate below the console card once focus had arrived on the card
        // from the title bar: every strategy and hint returned nothing while the link sat enabled, focusable and
        // directly underneath. Why the engine drops it is [X]. The fallback looks only in a straight line, so
        // pressing against the end of a row still leaves focus where it is.
        //
        // A container is not an answer either. Under RectilinearDistance (Add a console, Settings) the
        // ScrollViewer that encloses the page is at distance zero from everything in it, and the engine returned
        // it: focus went into the scroller, which draws no focus visual, so Up from "Search again" put the caret
        // nowhere visible and the discovered console above it could never be reached with a pad (2026-10-08).
        UIElement? engine = FocusManager.FindNextElement(winrtDirection, options) as UIElement;

        if (engine is not null && !IsStop(engine, FocusManager.GetFocusedElement(searchRoot.XamlRoot)))
        {
            engine = null;
        }

        // Up and Down go one row at a time. The engine ranks by alignment, so from a control at one end of a row
        // it passed over a nearer row whose controls sat at the other end: on About, Down from "Protocol spec"
        // skipped "Copy details" and "Open"; on Settings, Up from Configure skipped every row above the
        // controller test (which has nothing to press) and left the page for the gear (owner, with a pad,
        // 2026-10-09). The engine's answer stands when it is in the nearest row.
        if (direction is NavDirection.Up or NavDirection.Down
            && NearestRowCandidate(searchRoot, direction) is { } row
            && (engine is null || RowGap(searchRoot, engine, direction) > row.Gap + RowTolerance))
        {
            engine = row.Control;
        }

        if ((engine ?? StraightLineCandidate(searchRoot, direction)) is UIElement candidate)
        {
            // FocusState.Keyboard, never Programmatic: a Programmatic focus change does not draw the focus
            // visual, so directional navigation would move an invisible caret.
            candidate.Focus(FocusState.Keyboard);

            // A candidate below the fold is useless if the list does not scroll to it.
            candidate.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = AppMotion.Enabled });
            RevealPageEdge(candidate);
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

        // Nothing in that direction, and that is almost always correct: pressing against the end of a row
        // should leave focus where the user is pushing.
        //
        // There used to be a special case here for range controls, on the theory that a Slider would not
        // release focus vertically. It was wrong twice over. The original symptom was the tooltip bug (see
        // NavigablePopups), and what remained was the settings page's own geometry — its sliders end at x=996
        // and its toggles begin at x=1008, so they never overlap and the default XY rule cannot see one from
        // the other. That is fixed where it belongs, with an XY focus strategy on the page. The special case
        // never once fired against real markup: measured, the primary search always returned a candidate.
        // Removed rather than left as insurance for a cause that does not exist.
        if (FocusManager.GetFocusedElement(searchRoot.XamlRoot) is null)
        {
            seedFocus();
        }
    }

    /// <summary>
    /// The nearest focusable control in a straight line from the focused element, for when the engine's own
    /// search finds nothing: wholly beyond it in <paramref name="direction"/>, and overlapping it across that axis.
    /// Null when there is none, which is the usual and correct answer at the edge of a page.
    /// </summary>
    private static UIElement? StraightLineCandidate(UIElement searchRoot, NavDirection direction)
    {
        if (FocusManager.GetFocusedElement(searchRoot.XamlRoot) is not UIElement focused
            || BoundsIn(focused, searchRoot) is not { } from)
        {
            return null;
        }

        UIElement? best = null;
        double bestDistance = double.MaxValue;

        foreach (Control control in FocusableControls(searchRoot))
        {
            if (control == focused || IsWithin(control, focused) || IsWithin(focused, control)
                || BoundsIn(control, searchRoot) is not { } to)
            {
                continue;
            }

            double distance = direction switch
            {
                NavDirection.Down when to.Top >= from.Bottom && Overlaps(from.Left, from.Right, to.Left, to.Right)
                    => to.Top - from.Bottom,
                NavDirection.Up when to.Bottom <= from.Top && Overlaps(from.Left, from.Right, to.Left, to.Right)
                    => from.Top - to.Bottom,
                NavDirection.Right when to.Left >= from.Right && Overlaps(from.Top, from.Bottom, to.Top, to.Bottom)
                    => to.Left - from.Right,
                NavDirection.Left when to.Right <= from.Left && Overlaps(from.Top, from.Bottom, to.Top, to.Bottom)
                    => from.Left - to.Right,
                _ => double.MaxValue,
            };

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = control;
            }
        }

        return best;
    }

    private static bool Overlaps(double a0, double a1, double b0, double b1) => a0 < b1 && b0 < a1;

    /// <summary>
    /// How far apart two controls can sit vertically and still be one row: a button beside a text field is a few
    /// pixels lower than it, and a link row's icons sit a little off its text.
    /// </summary>
    private const double RowTolerance = 12;

    /// <summary>
    /// The closest control wholly above or below the focused one, by vertical gap; within a row, the one nearest
    /// across. Unlike <see cref="StraightLineCandidate"/> it does not need the two to overlap, because a page's
    /// rows do not line up: a link at the left and a button at the right are still one press apart.
    /// </summary>
    private static (Control Control, double Gap)? NearestRowCandidate(UIElement searchRoot, NavDirection direction)
    {
        if (FocusManager.GetFocusedElement(searchRoot.XamlRoot) is not UIElement focused
            || BoundsIn(focused, searchRoot) is not { } from)
        {
            return null;
        }

        List<(Control Control, double Gap, double Across)> beyond = [];
        double nearest = double.MaxValue;

        foreach (Control control in FocusableControls(searchRoot))
        {
            if (control == focused || IsWithin(control, focused) || IsWithin(focused, control)
                || BoundsIn(control, searchRoot) is not { } to
                || Gap(from, to, direction) is not { } gap)
            {
                continue;
            }

            double across = Math.Abs((to.Left + to.Right) / 2 - (from.Left + from.Right) / 2);
            beyond.Add((control, gap, across));
            nearest = Math.Min(nearest, gap);
        }

        (Control Control, double Gap)? best = null;
        double bestAcross = double.MaxValue;

        foreach ((Control control, double gap, double across) in beyond)
        {
            if (gap <= nearest + RowTolerance && across < bestAcross)
            {
                bestAcross = across;
                best = (control, gap);
            }
        }

        return best;
    }

    /// <summary>The vertical gap from the focused element to <paramref name="candidate"/>, for comparing rows.</summary>
    private static double RowGap(UIElement searchRoot, UIElement candidate, NavDirection direction)
        => FocusManager.GetFocusedElement(searchRoot.XamlRoot) is UIElement focused
            && BoundsIn(focused, searchRoot) is { } from
            && BoundsIn(candidate, searchRoot) is { } to
            && Gap(from, to, direction) is { } gap
                ? gap
                : double.MaxValue;

    /// <summary>
    /// How far <paramref name="to"/> lies beyond <paramref name="from"/> going up or down, or null when it is not
    /// beyond it. A few pixels of overlap still count, since stacked controls' bounds often touch.
    /// </summary>
    private static double? Gap(Rect from, Rect to, NavDirection direction) => direction switch
    {
        NavDirection.Down when to.Top >= from.Bottom - 2 => Math.Max(0, to.Top - from.Bottom),
        NavDirection.Up when to.Bottom <= from.Top + 2 => Math.Max(0, from.Top - to.Bottom),
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="candidate"/> is somewhere a person can see they are: a tab-stop control that is
    /// not a container and does not enclose the element focus is leaving. The same reading as
    /// <see cref="NeedsFocusSeed"/>, from the other side.
    /// </summary>
    private static bool IsStop(UIElement candidate, object? focused)
        => candidate is Control control && IsVisibleStop(control)
            && !(focused is DependencyObject from && IsWithin(from, candidate));

    /// <summary>
    /// A control that draws focus when it has it. A ScrollViewer takes focus whenever it can scroll, and a plain
    /// <see cref="ItemsControl"/> is a tab stop by default; neither draws a focus visual, and both sit exactly
    /// where their contents are, so a distance-ranked search prefers them to the items inside. The trace that
    /// found it (2026-10-08): Up from "Search again" went to <c>ItemsControl#DiscoveredList</c>, not to the
    /// console in it. A <see cref="Selector"/> (ListView, ComboBox) is a real stop and stays one.
    /// </summary>
    private static bool IsVisibleStop(Control control)
        => control is { IsEnabled: true, IsTabStop: true }
            and not ScrollViewer
            && control is not ItemsControl or Selector;

    private static Rect? BoundsIn(UIElement element, UIElement root)
    {
        if (element.ActualSize.X <= 0 || element.ActualSize.Y <= 0)
        {
            return null;
        }

        try
        {
            return element.TransformToVisual(root).TransformBounds(
                new Rect(0, 0, element.ActualSize.X, element.ActualSize.Y));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every enabled, visible tab stop under <paramref name="root"/>, skipping collapsed subtrees.</summary>
    private static IEnumerable<Control> FocusableControls(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            DependencyObject node = pending.Pop();
            if (node is UIElement { Visibility: Visibility.Collapsed })
            {
                continue;
            }

            if (node is Control control && IsVisibleStop(control))
            {
                yield return control;
            }

            int children = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < children; i++)
            {
                pending.Push(VisualTreeHelper.GetChild(node, i));
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

                // Seed focus onto the first item once the menu is up. Without this the menu opens with nothing
                // selected and the first Down is spent relative to the CARD underneath — which is outside the
                // popup and above it, so the search lands on the menu's SECOND item and the first press reads
                // as having been eaten. Reported from hardware exactly that way.
                void SeedFirstItem(object? sender, object args)
                {
                    flyout.Opened -= SeedFirstItem;

                    if (flyout is MenuFlyout { Items: { Count: > 0 } items })
                    {
                        foreach (MenuFlyoutItemBase item in items)
                        {
                            if (item is Control { IsTabStop: true, IsEnabled: true } control)
                            {
                                control.Focus(FocusState.Keyboard);
                                return;
                            }
                        }
                    }
                }

                flyout.Opened += SeedFirstItem;
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
    /// <summary>
    /// When focus reaches the first or last control a page can focus, scroll the page all the way to that end.
    ///
    /// <para>
    /// Bringing a control into view scrolls only as far as the control. Whatever sits above the first one or
    /// below the last one — a page title, a hero, a table of facts with nothing to press — was then out of reach
    /// with a pad: on About, once you had gone down, there was no way back to the top of the page (owner, with a
    /// pad, 2026-10-08). Arriving at the end of the focusable things is the moment someone wants the end of the
    /// page, so that is when it is shown.
    /// </para>
    /// </summary>
    private static void RevealPageEdge(UIElement focused)
    {
        if (NearestScrollViewer(focused) is not { Content: DependencyObject content } scroller)
        {
            return;
        }

        bool animate = AppMotion.Enabled;

        // The first or last ROW, not only the first or last control: About's top row is three links, and arriving
        // on the rightmost one from below is still arriving at the top.
        if (InSameRow(focused, FocusManager.FindFirstFocusableElement(content) as UIElement, scroller))
        {
            scroller.ChangeView(null, 0, null, disableAnimation: !animate);
        }
        else if (InSameRow(focused, FocusManager.FindLastFocusableElement(content) as UIElement, scroller))
        {
            scroller.ChangeView(null, scroller.ScrollableHeight, null, disableAnimation: !animate);
        }
    }

    private static bool InSameRow(UIElement focused, UIElement? edge, UIElement root)
        => edge is not null
            && (ReferenceEquals(focused, edge)
                || (BoundsIn(focused, root) is { } a && BoundsIn(edge, root) is { } b
                    && Math.Abs(a.Top - b.Top) <= RowTolerance));

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

        // The topmost popup only. A loop here read as "close them all" and could never do that - the
        // body returned on its first pass, leaving the decrement unreachable. One press dismisses one
        // layer, which is what Back means everywhere else.
        if (popups.Count == 0)
        {
            return false;
        }

        Popup top = popups[^1];

        // A dialog is closed through the dialog, never by shutting the popup it lives in. Shutting the popup
        // removed the prompt but not the dialog: ShowAsync never completed, the smoke layer stayed over the
        // window, and the Modal scope ModalHost pushed was never popped - so the shell, which keeps its hands
        // off while a modal owns focus, ignored every press after it. Found with a pad on 2026-10-08 as "B on
        // the Details dialog leaves the app dark and unresponsive"; Esc never did it, because Esc goes through
        // the dialog. Hide() is what Esc does: ShowAsync returns None, the same answer as the close button.
        if (FindDialog(top.Child) is { } dialog)
        {
            dialog.Hide();
            return true;
        }

        top.IsOpen = false;
        return true;
    }

    /// <summary>
    /// The <see cref="ContentDialog"/> a popup hosts, if it hosts one. The dialog is normally the popup's own
    /// child; the short descent covers a wrapper in between without searching a whole page's tree.
    /// </summary>
    private static ContentDialog? FindDialog(DependencyObject? root, int depth = 3)
    {
        if (root is ContentDialog dialog)
        {
            return dialog;
        }

        if (root is null || depth == 0)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindDialog(VisualTreeHelper.GetChild(root, i), depth - 1) is { } found)
            {
                return found;
            }
        }

        return null;
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