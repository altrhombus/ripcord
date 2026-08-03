using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System.Linq;
using Ripcord.Core.Input;
using Ripcord.Core.Settings;
using Ripcord.Input;
using Ripcord_App.Pages;

namespace Ripcord_App;

public sealed partial class MainWindow : Window
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
    private readonly ISettingsStore _settingsStore = new SettingsStore();

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

        _stickDeadzone = _settingsStore.Current.UiStickDeadzone;
        _settingsStore.Changed += s => _dispatcherQueue.TryEnqueue(() => _stickDeadzone = s.UiStickDeadzone);

        // NavigationView's IsSelected="True" on the Consoles item marks it selected but does not fire
        // SelectionChanged on startup, so the frame would otherwise stay empty until the user clicks.
        NavFrame.Navigate(typeof(ConsolesPage));

        // GoBack() navigates the Frame's content directly, bypassing NavigationView's selection state, so the
        // pane would keep highlighting whatever was selected before.
        NavFrame.Navigated += NavFrame_Navigated;

        // Deferred to NavView.Loaded rather than the constructor: creating a GameInput-backed WinRT component
        // before the window content is realized was implicated in an early native crash (combase.dll,
        // E_UNEXPECTED) on the first D-pad press.
        NavView.Loaded += (_, _) =>
        {
            _navControllerSource = new GameInputControllerSource();
            _navControllerSubscription = _navControllerSource.StateChanges(string.Empty).Subscribe(
                new AnonymousObserver<ControllerStateFrame>(OnControllerState));

            // FocusManager.TryMoveFocus moves focus relative to whatever already has it; with nothing focused,
            // directional input has no anchor and silently does nothing. A hardware arrow key goes through a
            // different WinUI path that picks an initial target itself — TryMoveFocus has no such fallback.
            FocusFirstNavItem();
        };

        Closed += (_, _) =>
        {
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
    public void ShowStream(object console)
    {
        StreamFrame.Navigate(typeof(SessionPage), console);
        StreamFrame.Visibility = Visibility.Visible;

        // Hide the chrome underneath so nothing renders behind the video and no nav item is left looking
        // selected while a stream — which is not a nav destination — is running.
        NavView.Visibility = Visibility.Collapsed;
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

        StreamFrame.Visibility = Visibility.Collapsed;

        // Clearing the content unloads SessionPage, which is what triggers its async teardown. The navigation
        // stacks are cleared too, so repeated sessions don't accumulate history in a frame that is only ever
        // navigated forwards.
        StreamFrame.Content = null;
        StreamFrame.BackStack.Clear();
        StreamFrame.ForwardStack.Clear();

        NavView.Visibility = Visibility.Visible;
        TitleBarRow.Height = new GridLength(48);
        AppTitleBar.Visibility = Visibility.Visible;

        // The consoles list is the page revealed underneath after a stream (nothing else navigates during one),
        // and returning to it never re-fired Loaded — so a just-rested console kept its stale "Online" dot.
        // Nudge it to re-probe now, handing over the rest intent so it can watch that console settle.
        if (NavFrame.Content is ConsolesPage consolesPage)
        {
            consolesPage.OnReturnedFromStream(closedConsoleHost, restRequested);
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

    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        if (e.SourcePageType == typeof(SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        string? tag = e.SourcePageType.Name switch
        {
            nameof(ConsolesPage) => "consoles",
            nameof(AboutPage) => "about",
            _ => null,
        };

        if (tag is null)
        {
            return;
        }

        foreach (var menuItem in NavView.MenuItems)
        {
            if (menuItem is NavigationViewItem { Tag: string itemTag } item && itemTag == tag)
            {
                NavView.SelectedItem = item;
                return;
            }
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        switch (item.Tag)
        {
            case "consoles":
                NavFrame.Navigate(typeof(ConsolesPage));
                break;
            case "about":
                NavFrame.Navigate(typeof(AboutPage));
                break;
            default:
                // An unknown tag is a wiring mistake, but throwing would take the whole app down over a
                // navigation click. Ignore it; the pane simply does not move.
                System.Diagnostics.Debug.WriteLine($"Unknown navigation item tag: {item.Tag}");
                break;
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

                if (eastPressed && NavFrame.CanGoBack)
                {
                    NavFrame.GoBack();
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

        if (winrtDirection == FocusNavigationDirection.None || Content is not { } searchRoot)
        {
            return;
        }

        // The simple TryMoveFocus(direction) overload throws COMException 0x8000FFFF in a WinUI Desktop app (as
        // opposed to UWP, where a single implicit CoreWindow root makes it valid) — a desktop app can host
        // multiple windows, so FindNextElementOptions.SearchRoot must say which visual tree to search.
        var options = new FindNextElementOptions { SearchRoot = searchRoot };

        // If there is nothing in that direction, leave focus where it is. Previously this fell back to
        // FocusFirstNavItem(), so pressing against the edge of a list teleported focus to the navigation pane.
        // Only seed focus when nothing has it at all.
        if (!FocusManager.TryMoveFocus(winrtDirection, options)
            && FocusManager.GetFocusedElement(Content.XamlRoot) is null)
        {
            FocusFirstNavItem();
        }
    }

    private void FocusFirstNavItem()
    {
        if (NavView.SelectedItem is Control selected)
        {
            selected.Focus(FocusState.Programmatic);
            return;
        }

        if (NavView.MenuItems.OfType<Control>().FirstOrDefault() is { } firstItem)
        {
            firstItem.Focus(FocusState.Programmatic);
        }
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
    }
}
