using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.Generic;
using System.Linq;
using Ripcord.Core.Consoles;
using Ripcord.Core.Input;
using Ripcord.Core.Settings;
using Ripcord.Input;
using Ripcord.Presentation;
using Ripcord_App.Pages;
using Ripcord_App.Services;
using Ripcord.Core.Reactive;

namespace Ripcord_App;

/// <summary>
/// The application shell: navigation chrome, the stream layer above it, and gamepad-driven focus movement.
///
/// <para>
/// It implements <see cref="IShellNavigator"/> so that pages ask the portable seam for window-level operations
/// rather than casting a static back to this class. The three members that satisfies were already public methods
/// here; naming them as an interface is what lets a page stop knowing which window it is inside.
/// </para>
/// </summary>
public sealed partial class MainWindow : Window, IShellNavigator
{
    private enum NavDirection
    {
        None,
        Up,
        Down,
        Left,
        Right,
    }

    /// <summary>
    /// Directional auto-repeat. Without it, holding a stick or D-pad moved focus exactly once, so navigating a
    /// long list meant flicking repeatedly — the single most obviously-wrong thing about gamepad navigation.
    /// The initial pause prevents an intended single step from becoming two.
    /// </summary>
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly ISettingsStore _settingsStore = App.Services.Settings;

    // Separate from SessionPage's own controller source: this one drives app-chrome navigation (focus movement,
    // activating buttons), not console input passthrough — conceptually different consumers of the same pad.
    private GameInputControllerSource? _navControllerSource;
    private IDisposable? _navControllerSubscription;
    private ControllerButtons _previousButtons = ControllerButtons.None;
    private NavDirection _heldDirection = NavDirection.None;
    private DateTimeOffset _directionHeldSince;
    private DateTimeOffset _lastRepeat;

    private double _stickDeadzone;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // The backdrop is declared in markup, which cannot ask whether the user permits transparency — so the
        // first thing done with it is to re-decide it. Without this, someone who has turned transparency off
        // still got Mica until the first time they closed a stream.
        RestoreBackdrop();
        AppEffects.Changed += OnEffectsChanged;

        _stickDeadzone = _settingsStore.Current.UiStickDeadzone;
        _settingsStore.Changed += s => _dispatcherQueue.TryEnqueue(() => _stickDeadzone = s.UiStickDeadzone);

        ChromeFrame.Navigate(typeof(ConsolesPage));

        // Deferred to Loaded rather than run in the constructor: creating a GameInput-backed WinRT component
        // before the window content is realized was implicated in an early native crash (combase.dll,
        // E_UNEXPECTED) on the first D-pad press.
        ChromeFrame.Loaded += (_, _) =>
        {
            _navControllerSource = new GameInputControllerSource();
            _navControllerSubscription = _navControllerSource.StateChanges(string.Empty).Subscribe(
                new AnonymousObserver<ControllerStateFrame>(OnControllerState));

            // FocusManager.FindNextElement moves focus relative to whatever already has it; with nothing
            // focused, directional input has no anchor and silently does nothing. A hardware arrow key goes
            // through a different WinUI path that picks an initial target itself — this one has no such
            // fallback, so something has to be focused before a pad can move.
            //
            // Posted at low priority rather than run here: this frame's Loaded fires before its content page
            // has populated, so asking the page what to focus now gets the answer it has before it has loaded
            // anything — which is how the first focus stop ended up on "Add console" instead of a console.
            _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusFirstContentElement);
        };

        Closed += (_, _) =>
        {
            AppEffects.Changed -= OnEffectsChanged;
            _navControllerSubscription?.Dispose();
            _navControllerSource?.Dispose();
        };
    }

    // ---- stream layer ----

    /// <summary>True while the stream layer is showing.</summary>
    public bool IsStreaming => StreamFrame.Visibility == Visibility.Visible;

    /// <summary>True while the window is in the fullscreen presenter.</summary>
    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    /// <summary>
    /// Show the stream layer for <paramref name="console"/>. Navigation into it is a window-level operation
    /// rather than a Frame navigation, because the stream is a separate layer above the chrome.
    /// </summary>
    public void ShowStream(PairedConsole console)
    {
        StreamFrame.Navigate(typeof(SessionPage), console);
        StreamFrame.Visibility = Visibility.Visible;

        // Drop the backdrop for the duration. The window is about to be covered by opaque video, so Mica would
        // be blurring the wallpaper behind it every frame for something nobody can see — invisible on screen,
        // measurable in power, and this app's whole point is running on a handheld.
        SystemBackdrop = null;

        // Hide the chrome underneath so nothing renders behind the video.
        ChromeFrame.Visibility = Visibility.Collapsed;
        ApplyStreamChrome();
    }

    /// <summary>
    /// Tear the stream layer down and restore normal navigation. <paramref name="closedConsoleHost"/> and
    /// <paramref name="restRequested"/> carry the just-ended session's console and whether it was asked to rest,
    /// so the consoles list can re-probe on return (its own <c>Loaded</c> only fires once) and show a
    /// rest-requested console settling.
    /// </summary>
    public void CloseStream(string? closedConsoleHost = null, bool restRequested = false)
    {
        SetFullScreen(false);
        RestoreBackdrop();

        StreamFrame.Visibility = Visibility.Collapsed;

        // Clearing the content unloads SessionPage, which is what triggers its async teardown. The navigation
        // stacks are cleared too, so repeated sessions don't accumulate history in a frame that is only ever
        // navigated forwards.
        StreamFrame.Content = null;
        StreamFrame.BackStack.Clear();
        StreamFrame.ForwardStack.Clear();

        ChromeFrame.Visibility = Visibility.Visible;
        TitleBarRow.Height = new GridLength(48);
        AppTitleBar.Visibility = Visibility.Visible;

        // The consoles list is the page revealed underneath after a stream (nothing else navigates during one),
        // and returning to it never re-fired Loaded — so a just-rested console kept its stale "Online" dot.
        // Nudge it to re-probe now, handing over the rest intent so it can watch that console settle.
        if (ChromeFrame.Content is ConsolesPage consolesPage)
        {
            consolesPage.OnReturnedFromStream(closedConsoleHost, restRequested);
        }
    }

    /// <summary>
    /// Put the backdrop back after a stream, honouring the transparency setting.
    ///
    /// <para>
    /// Mica is a transparency effect, so a user who has turned those off should not get one — that setting is
    /// frequently chosen because the effect makes text harder to read, and the chrome is perfectly legible on a
    /// solid surface. Re-read rather than remembered, because it can change while a stream is running.
    /// </para>
    /// </summary>
    private void RestoreBackdrop()
        => SystemBackdrop = AppEffects.TransparencyEnabled ? new MicaBackdrop() : null;

    /// <summary>
    /// Transparency or high contrast changed while running. Only the backdrop needs acting on here — the accent
    /// wash and the family marks resolve through <c>AccentResources</c> on each binding pass, so they follow on
    /// the next render without anything being told.
    /// </summary>
    private void OnEffectsChanged()
    {
        // Not while streaming: the backdrop is deliberately off for the duration, and putting one back under
        // opaque video would undo the reason it was dropped.
        if (!IsStreaming)
        {
            RestoreBackdrop();
        }
    }

    /// <summary>
    /// Enter or leave fullscreen. Owned here because the presenter belongs to the window, not to a page.
    /// </summary>
    public void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == IsFullScreen)
        {
            return;
        }

        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);

        if (IsStreaming)
        {
            ApplyStreamChrome();
        }
    }

    /// <summary>
    /// Chrome for the stream layer. Fullscreen covers the title bar row entirely; windowed keeps the title bar
    /// visible so the window can still be moved, resized and closed while the stream keeps running.
    /// </summary>
    private void ApplyStreamChrome()
    {
        bool fullScreen = IsFullScreen;

        TitleBarRow.Height = fullScreen ? new GridLength(0) : new GridLength(48);
        AppTitleBar.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;

        Grid.SetRow(StreamFrame, fullScreen ? 0 : 1);
        Grid.SetRowSpan(StreamFrame, fullScreen ? 2 : 1);
    }

    private void TitleBar_BackRequested(TitleBar sender, object args) => GoBack();

    /// <summary>
    /// The single back route. Every gesture that means "back" — the title-bar chevron and the pad's East
    /// button — comes through here, so they cannot disagree about what back does.
    /// </summary>
    private void GoBack()
    {
        if (ChromeFrame.CanGoBack)
        {
            ChromeFrame.GoBack();
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => NavigateToUtility(typeof(SettingsPage));

    private void OnAboutClick(object sender, RoutedEventArgs e) => NavigateToUtility(typeof(AboutPage));

    /// <summary>
    /// Open one of the two utilities.
    ///
    /// <para>
    /// Navigation rather than a pane selection, which is the substantive difference from the NavigationView
    /// this replaced: opening Settings now pushes onto the back stack, so the chevron and B both return you to
    /// what you were doing. Under the pane there was no back — you had to notice which item to click.
    /// </para>
    ///
    /// <para>
    /// Re-entry is ignored so that pressing the same command twice does not stack duplicates behind you, which
    /// would make back feel broken in exactly the way a repeated click invites.
    /// </para>
    /// </summary>
    private void NavigateToUtility(Type pageType)
    {
        if (ChromeFrame.Content?.GetType() != pageType)
        {
            ChromeFrame.Navigate(pageType);
        }
    }

    private void OnControllerState(ControllerStateFrame frame)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // A background input-polling handler must never be able to take the whole process down.
            try
            {
                // A live streaming session owns the controller — SessionPage forwards input to the console.
                // Don't let app-chrome navigation consume the same pad, or B would exit the stream instead of
                // reaching the console. SessionPage provides its own exit gesture so this is not a trap.
                // Keep the edge-trackers current so returning to chrome navigation later doesn't fire a stale
                // rising edge.
                if (StreamFrame.Content is SessionPage { IsCapturingInput: true })
                {
                    _previousButtons = frame.Buttons;
                    _heldDirection = NavDirection.None;
                    return;
                }

                HandleDirectionalNavigation(frame);

                bool southPressed = IsRisingEdge(frame.Buttons, ControllerButtons.South);
                bool eastPressed = IsRisingEdge(frame.Buttons, ControllerButtons.East);
                _previousButtons = frame.Buttons;

                if (southPressed)
                {
                    ActivateFocusedElement();
                }

                if (eastPressed && ChromeFrame.CanGoBack)
                {
                    ChromeFrame.GoBack();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Gamepad UI navigation error: {ex}");
            }
        });
    }

    /// <summary>Move focus on a fresh press, then repeat while the direction stays held.</summary>
    private void HandleDirectionalNavigation(in ControllerStateFrame frame)
    {
        NavDirection direction = GetNavDirection(frame);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (direction == NavDirection.None)
        {
            _heldDirection = NavDirection.None;
            return;
        }

        if (direction != _heldDirection)
        {
            _heldDirection = direction;
            _directionHeldSince = now;
            _lastRepeat = now;
            MoveFocus(direction);
            return;
        }

        if (now - _directionHeldSince < RepeatDelay || now - _lastRepeat < RepeatInterval)
        {
            return;
        }

        _lastRepeat = now;
        MoveFocus(direction);
    }

    private bool IsRisingEdge(ControllerButtons current, ControllerButtons button) =>
        (current & button) != 0 && (_previousButtons & button) == 0;

    private NavDirection GetNavDirection(in ControllerStateFrame frame)
    {
        float deadzone = (float)_stickDeadzone;

        if ((frame.Buttons & ControllerButtons.DPadUp) != 0 || frame.LeftStickY > deadzone)
        {
            return NavDirection.Up;
        }

        if ((frame.Buttons & ControllerButtons.DPadDown) != 0 || frame.LeftStickY < -deadzone)
        {
            return NavDirection.Down;
        }

        if ((frame.Buttons & ControllerButtons.DPadLeft) != 0 || frame.LeftStickX < -deadzone)
        {
            return NavDirection.Left;
        }

        if ((frame.Buttons & ControllerButtons.DPadRight) != 0 || frame.LeftStickX > deadzone)
        {
            return NavDirection.Right;
        }

        return NavDirection.None;
    }

    private void MoveFocus(NavDirection direction)
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

        // If there is nothing in that direction, leave focus where it is. An earlier version re-seeded focus
        // here, so pressing against the edge of a list teleported the caret away from where the user was
        // pushing. Only seed when nothing has focus at all.
        if (FocusManager.GetFocusedElement(searchRoot.XamlRoot) is null)
        {
            FocusFirstContentElement();
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
    private FrameworkElement? DirectionalRoot()
    {
        if (Content is not FrameworkElement content)
        {
            return null;
        }

        if (content.XamlRoot is not { } xamlRoot)
        {
            return content;
        }

        IReadOnlyList<Popup> popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);
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

    /// <summary>
    /// Seed focus inside the page content.
    ///
    /// <para>
    /// <b>Content, not chrome</b>, and that is the point of the change rather than a side effect of it. The
    /// first focus stop used to be the navigation pane, so a player opening the app with a pad in hand had to
    /// travel out of the furniture before reaching a console. Now the first thing focused is the first thing
    /// on the page — which on the console list is a console.
    /// </para>
    /// </summary>
    private void FocusFirstContentElement()
    {
        if (ChromeFrame.Content is not FrameworkElement content)
        {
            return;
        }

        // FocusState.Keyboard everywhere below, never Programmatic — see the note in MoveFocus. Seeding focus
        // programmatically draws no focus visual, so the app's initial focus was invisible.

        // The page's own answer first: it knows which element is the point of the page, and tree order does
        // not. On the console list that is a console rather than the "Add console" button above it.
        // Focus() reports whether it landed. A GridView that has not realised any items yet accepts the call
        // and focuses nothing, which would otherwise leave the app with no focus at all — worse than the
        // fallback, because directional input needs an anchor to move from.
        if (content is IInitialFocusTarget target
            && target.InitialFocus is { } preferred
            && preferred.Focus(FocusState.Keyboard))
        {
            return;
        }

        if (FocusManager.FindFirstFocusableElement(content) is Control first)
        {
            first.Focus(FocusState.Keyboard);
            return;
        }

        // Nothing focusable on the page yet (it may still be populating). The title-bar commands are always
        // there, so focus is at least somewhere a pad can move from.
        SettingsButton.Focus(FocusState.Keyboard);
    }

    private void ActivateFocusedElement()
    {
        var xamlRoot = Content?.XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        if (FocusManager.GetFocusedElement(xamlRoot) is not FrameworkElement focused)
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
}
