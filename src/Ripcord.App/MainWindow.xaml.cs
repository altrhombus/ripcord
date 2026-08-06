using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Input;
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
using Ripcord_App.Controls;
using Ripcord_App.Input;
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
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly ISettingsStore _settingsStore = App.Services.Settings;

    /// <summary>
    /// The app's one reader of the controller. There used to be two — a GameInput-only source here for menu
    /// navigation and a full composite in the session page — which is why a DualSense worked in a stream and
    /// did nothing in the menus.
    /// </summary>
    private readonly InputRouter _input = App.Input;

    /// <summary>
    /// The chrome's claim on the pad, pushed for the life of the window. A stream pushes a Session scope over
    /// it; a dialog will push a Modal scope over that.
    ///
    /// <para>
    /// Its activation edge re-seeds focus, which is what makes returning from a stream land somewhere. Before
    /// the scope stack there was no edge to hang that on — the stream simply stopped claiming the pad and
    /// whatever had focus before was long gone.
    /// </para>
    /// </summary>
    private readonly ShellInputScope _chromeScope;

    /// <summary>
    /// Turns intents into focus movement, activation, scrolling and context menus. Takes the content root as a
    /// callback rather than the window itself, so the same pilot can later be pointed at a dialog's root
    /// without knowing it moved.
    /// </summary>
    private readonly FocusPilot _focus;

    /// <summary>Re-seeds focus whenever it goes missing. See <see cref="FocusWatchdog"/> for why it is central.</summary>
    private readonly FocusWatchdog _focusWatchdog;

    /// <summary>
    /// Controller text entry. Owned by the window rather than by each surface that has a text field: it is a
    /// property of the input method, not of the page, and one instance is what keeps two from ever being up.
    /// </summary>
    private readonly SoftKeyboardOverlay _softKeyboard = new();

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // The TitleBar must not be a focus target itself. Directional focus was picking the control rather than
        // the Settings/About buttons inside its RightHeader, which draws no focus visual and offers nothing to
        // activate — so pressing Up off the console grid looked like focus vanishing, and the two commands were
        // unreachable by pad. Its children stay focusable; only the container stops volunteering.
        AppTitleBar.IsTabStop = false;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // The backdrop is declared in markup, which cannot ask whether the user permits transparency — so the
        // first thing done with it is to re-decide it. Without this, someone who has turned transparency off
        // still got Mica until the first time they closed a stream.
        RestoreBackdrop();
        AppEffects.Changed += OnEffectsChanged;

        _settingsStore.Changed += s =>
            _dispatcherQueue.TryEnqueue(() => _input.UseDeadzone(s.UiStickDeadzone));

        _focus = new FocusPilot(
            contentRoot: () => Content as FrameworkElement,
            seedFocus: FocusFirstContentElement,
            openTextEntry: OpenSoftKeyboard);

        _chromeScope = new ShellInputScope(
            InputScopeKind.Chrome,
            onActivated: () => _dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low, FocusFirstContentElement),
            focusRoot: () => Content?.XamlRoot);

        // The standing guarantee that something is always focused. Everything else that seeds focus — window
        // activation, navigation, the first pad press — predates it and each was written for one situation
        // somebody hit; this one covers the situations nobody has hit yet.
        _focusWatchdog = new FocusWatchdog(
            _dispatcherQueue,
            needsSeed: () => _focus.NeedsFocusSeed(),
            seed: FocusFirstContentElement);

        // Focus is seeded on every navigation, not only when the chrome scope activates. Going to the pair flow
        // and back left nothing focused, so a pad user had to press a direction just to get the caret back onto
        // the console list — the new page has no idea the old one's focused element went away with it.
        ChromeFrame.Navigated += (_, _) =>
            _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusFirstContentElement);

        ChromeFrame.Navigate(typeof(ConsolesPage));

        // Deferred to Loaded rather than run in the constructor: creating a GameInput-backed WinRT component
        // before the window content is realized was implicated in an early native crash (combase.dll,
        // E_UNEXPECTED) on the first D-pad press.
        ChromeFrame.Loaded += (_, _) =>
        {
            _input.IntentReceived += OnNavIntent;
            _input.Start();

            // Pushing the chrome scope is what seeds initial focus, via its activation edge. Focus has to be
            // somewhere before a pad can move: FindNextElement works relative to whatever already has focus,
            // and with nothing focused directional input silently does nothing. (A hardware arrow key goes
            // through a different WinUI path that picks its own initial target; this one has no such fallback.)
            _input.Scopes.Push(_chromeScope);
            _focusWatchdog.Start();

            // The bar draws whatever the top scope declares and redraws on the two edges that can change it:
            // a different scope taking over, and the user picking up a different input device. It is never
            // told about pages.
            HintBar.SetPadFamily(_input.PadFamily);
            HintBar.SetMode(_input.Mode);
            HintBar.Show(_input.Scopes.Top?.Prompts);

            _input.Scopes.TopChanged += scope =>
                _dispatcherQueue.TryEnqueue(() => HintBar.Show(scope?.Prompts));

            _input.ModeChanged += mode =>
                _dispatcherQueue.TryEnqueue(() => HintBar.SetMode(mode));

            // Connection events arrive on a polling thread, hence the marshal — the router says so.
            _input.PadFamilyChanged += family =>
                _dispatcherQueue.TryEnqueue(() => HintBar.SetPadFamily(family));
        };

        // What the other two input methods look like, for the mode tracker. Handled events count too: a click
        // that a button consumes is still the user reaching for a mouse, and a mode that ignored consumed input
        // would only ever notice presses that hit nothing. Pointer MOVEMENT is deliberately not reported — a
        // window appearing under a stationary cursor generates it, which is the least intentional signal there
        // is.
        if (Content is UIElement contentRoot)
        {
            contentRoot.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler((_, e) =>
                {
                    if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
                    {
                        _input.ReportTouchActivity();
                    }
                    else
                    {
                        _input.ReportPointerActivity();
                    }
                }),
                handledEventsToo: true);

            contentRoot.AddHandler(
                UIElement.KeyDownEvent,
                new KeyEventHandler((_, _) => _input.ReportKeyboardActivity()),
                handledEventsToo: true);
        }

        AddAccelerators();

        // Coming back from another app, focus is often gone — WinUI does not restore it, and a pad user is then
        // looking at a window with no caret, pressing a direction that has nothing to move from. Re-seeded on
        // activation, at low priority so the visual tree has settled first.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                _dispatcherQueue.TryEnqueue(
                    DispatcherQueuePriority.Low,
                    () => SeedFocusIfNothingHasIt());
            }
        };

        Closed += (_, _) =>
        {
            AppEffects.Changed -= OnEffectsChanged;
            _input.IntentReceived -= OnNavIntent;
            _input.Dispose();
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
    /// <summary>
    /// Window-level keyboard shortcuts.
    ///
    /// <para>
    /// In code rather than markup for one unavoidable reason and one consequence of it: <c>VirtualKey</c> has
    /// no name for the comma, so <c>Ctrl+,</c> — the shortcut every Windows app uses for settings — can only be
    /// written as its numeric code, and once one accelerator lives here the rest are easier to read beside it
    /// than split across two files.
    /// </para>
    ///
    /// <para>
    /// Deliberately no <c>TabIndex</c> anywhere in this app: tab order follows declaration order, so
    /// rearranging markup cannot silently produce a wrong order that only a keyboard user discovers. Where the
    /// order is wrong, the markup moves.
    /// </para>
    /// </summary>
    private void AddAccelerators()
    {
        if (Content is not UIElement root)
        {
            return;
        }

        // Alt+Left/Right — the platform's back and forward, and what a browser-shaped muscle memory reaches for.
        Add(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Menu, GoBack);
        Add(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.Menu, () =>
        {
            if (ChromeFrame.CanGoForward)
            {
                ChromeFrame.GoForward();
            }
        });

        // Ctrl+, for settings, Ctrl+N for the thing this app is for.
        Add((Windows.System.VirtualKey)188, Windows.System.VirtualKeyModifiers.Control, () => NavigateToUtility(typeof(SettingsPage)));
        Add(Windows.System.VirtualKey.N, Windows.System.VirtualKeyModifiers.Control, () =>
        {
            if (!Shell().IsStreaming)
            {
                ChromeFrame.Navigate(typeof(AddConsolePage));
            }
        });

        // F6 cycles between the window's two focus regions — the title-bar commands and the page. With the
        // NavigationView gone there are only two, which is exactly what makes F6 worth having: there is no
        // other key that reaches the title bar.
        Add(Windows.System.VirtualKey.F6, Windows.System.VirtualKeyModifiers.None, CycleRegion);

        void Add(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Action invoke)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };

            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                invoke();
            };

            root.KeyboardAccelerators.Add(accelerator);
        }
    }

    private IShellNavigator Shell() => this;

    /// <summary>
    /// Move focus between the title bar's commands and the page content.
    ///
    /// <para>
    /// Asks where focus is rather than remembering where it was put, so it stays correct when something else
    /// moves focus — a dialog closing, the watchdog re-seeding — which a toggle field would not.
    /// </para>
    /// </summary>
    private void CycleRegion()
    {
        if (Content?.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        bool inTitleBar = FocusManager.GetFocusedElement(xamlRoot) is DependencyObject focused
                          && IsDescendantOf(focused, AppTitleBar);

        if (inTitleBar)
        {
            FocusFirstContentElement();
            return;
        }

        SettingsButton.Focus(FocusState.Keyboard);
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void GoBack()
    {
        // Closing what is on top comes first. Otherwise Back with a context menu open navigates the page
        // underneath it, which is both surprising and leaves the menu on screen.
        if (_focus.TryDismissPopup())
        {
            return;
        }

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

    /// <summary>
    /// Act on one navigation intent. Reached only while a non-Session scope owns the pad — the router does
    /// that arbitration, so this no longer has to know what is in the stream layer or ask a page for a bool.
    /// </summary>
    private void OnNavIntent(NavIntent intent)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // A background input-polling handler must never be able to take the whole process down.
            try
            {
                // Reported here rather than from the router's frame handler so the tracker stays on one
                // thread — and an intent is precisely the "deliberate activity" its contract asks for.
                _input.ReportControllerActivity();

                // Nothing focused: this press establishes the caret and goes no further. Directional movement
                // is relative to whatever has focus, so without an anchor the press was simply lost — which is
                // why the first one or two after launch or re-activation felt like they did nothing.
                // Consuming it is the predictable behaviour: one press to show where you are, the next to move.
                if (SeedFocusIfNothingHasIt())
                {
                    return;
                }

                if (intent.Direction != NavDirection.None)
                {
                    _focus.MoveFocus(intent.Direction);
                }

                if (intent.Scroll != 0)
                {
                    _focus.Scroll(intent.Scroll);
                }

                if (intent.Accept)
                {
                    _focus.ActivateFocusedElement();
                }

                if (intent.Context)
                {
                    _focus.OpenContextMenu();
                }

                if (intent.Back)
                {
                    GoBack();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Gamepad UI navigation error: {ex}");
            }
        });
    }

    /// <summary>
    /// Put the soft keyboard up for a text field, and report whether it went.
    ///
    /// <para>
    /// Only in Controller mode. With a keyboard or a mouse in hand the user has a better way to type than
    /// aiming at glyphs, and an overlay covering the form they are filling in would be an obstruction rather
    /// than a help — the same reason the hint bar hides outside Controller mode.
    /// </para>
    /// </summary>
    private bool OpenSoftKeyboard(Control target)
    {
        if (_input.Mode != InputMode.Controller)
        {
            return false;
        }

        _ = _softKeyboard.ShowAsync(target);
        return true;
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
    /// <summary>
    /// Put focus somewhere if it is nowhere. Returns true when it had to act, so a caller can treat the press
    /// that prompted it as spent.
    /// </summary>
    private bool SeedFocusIfNothingHasIt()
    {
        if (!_focus.NeedsFocusSeed())
        {
            return false;
        }

        FocusFirstContentElement();
        return true;
    }

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

        // Descend past containers. FindFirstFocusableElement happily returns a ScrollViewer — one is focusable
        // whenever it can scroll — and focusing a container is a TRAP, not merely untidy: directional search
        // starts from the focused element, and a container that ENCLOSES every candidate has nothing above,
        // below or beside it, so every direction finds nothing and focus can never leave. That is exactly what
        // made the settings page unusable with a pad.
        DependencyObject scope = content;
        for (int depth = 0; depth < 8; depth++)
        {
            if (FocusManager.FindFirstFocusableElement(scope) is not Control first)
            {
                break;
            }

            if (first is not ScrollViewer scroller)
            {
                first.Focus(FocusState.Keyboard);
                return;
            }

            if (scroller.Content is not DependencyObject inner)
            {
                break;
            }

            scope = inner;
        }

        // Nothing focusable on the page yet (it may still be populating). The title-bar commands are always
        // there, so focus is at least somewhere a pad can move from.
        SettingsButton.Focus(FocusState.Keyboard);
    }

}
