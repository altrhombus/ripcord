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
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Setup;
using Ripcord.Core.Launch;
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
/// rather than casting a static back to this class, and a page does not know which window it is inside.
/// </para>
/// </summary>
public sealed partial class MainWindow : Window, IShellNavigator
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly ISettingsStore _settingsStore = App.Services.Settings;

    /// <summary>
    /// The app's one reader of the controller. The menus and the session page share it, so a pad that works in
    /// a stream works in the menus.
    /// </summary>
    private readonly InputRouter _input = App.Input;

    /// <summary>
    /// The chrome's claim on the pad, pushed for the life of the window. A stream pushes a Session scope over
    /// it; a dialog pushes a Modal scope over that.
    ///
    /// <para>
    /// Its activation edge re-seeds focus, which is what makes returning from a stream land somewhere.
    /// </para>
    /// </summary>
    private readonly ShellInputScope _chromeScope;

    /// <summary>
    /// Turns intents into focus movement, activation, scrolling and context menus. Takes the content root as a
    /// callback rather than the window itself, so it does not depend on which root it is pointed at.
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

        // The TitleBar must not be a focus target itself. Directional focus otherwise picks the control rather
        // than the Settings/About buttons inside its RightHeader; it draws no focus visual and offers nothing to
        // activate, so pressing Up off the console grid looks like focus vanishing, and the two commands are
        // unreachable by pad. Its children stay focusable; only the container stops volunteering.
        AppTitleBar.IsTabStop = false;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        ApplyAppIcon();

        // The backdrop is declared in markup, which cannot ask whether the user permits transparency — so the
        // first thing done with it is to re-decide it. Without this, someone who has turned transparency off
        // still gets Mica until the first time they close a stream.
        RestoreBackdrop();
        AppEffects.Changed += OnEffectsChanged;

        PaintCaptionButtons();
        if (Content is FrameworkElement themedRoot)
        {
            themedRoot.ActualThemeChanged += (_, _) => PaintCaptionButtons();

            // The minimum is in physical pixels, so it needs the scale, which exists once the content is loaded.
            themedRoot.Loaded += (_, _) =>
            {
                ApplyMinimumSize();
                if (themedRoot.XamlRoot is { } xamlRoot)
                {
                    xamlRoot.Changed += (_, _) => ApplyMinimumSize();
                }
            };
        }

        _settingsStore.Changed += s =>
            Post(() =>
            {
                _input.UseDeadzone(s.UiStickDeadzone);
                UseNavigationSounds(_input.Mode);
            });

        _focus = new FocusPilot(
            contentRoot: () => Content as FrameworkElement,
            seedFocus: FocusFirstContentElement,
            openTextEntry: OpenSoftKeyboard);

        _chromeScope = new ShellInputScope(
            InputScopeKind.Chrome,
            onActivated: () => Post(
                DispatcherQueuePriority.Low, FocusFirstContentElement),
            focusRoot: () => Content?.XamlRoot);

        // The standing guarantee that something is always focused. The other seeds — window activation,
        // navigation, the first pad press — each cover one situation; this one covers the rest.
        // needsSeed is the SHELL's question, not the pilot's: asking the pilot directly would skip the modal
        // guard and make the watchdog the one seeding path with nothing in front of it. See ModalOwnsFocus.
        _focusWatchdog = new FocusWatchdog(
            _dispatcherQueue,
            needsSeed: ShellShouldSeedFocus,
            seed: FocusFirstContentElement);

        // Focus is seeded on every navigation, not only when the chrome scope activates. Going to the pair flow
        // and back otherwise leaves nothing focused, and a pad user has to press a direction just to get the
        // caret back onto the console list — the new page has no idea the old one's focused element went away
        // with it.
        ChromeFrame.Navigated += (_, _) =>
        {
            _arrivalSeedPending = true;
            Post(DispatcherQueuePriority.Low, FocusFirstContentElement);
        };

        NavigateToFirstPage();

        // Both, because a bar going away collapses without reliably reporting a size change.
        HintBar.SizeChanged += (_, _) => PushBottomInset();
        HintBar.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => PushBottomInset());

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
            HintBar.SetPadAttached(_input.PadAttached);
            HintBar.SetMode(_input.Mode);
            HintBar.Show(_input.Scopes.Top?.Prompts);

            _input.Scopes.TopChanged += scope =>
                Post(() => HintBar.Show(scope?.Prompts));

            _input.ModeChanged += mode =>
                Post(() =>
                {
                    HintBar.SetMode(mode);
                    UseNavigationSounds(mode);
                });
            UseNavigationSounds(_input.Mode);

            // A's verb follows focus: "Play" on a card, "Toggle" on a switch, "Type" in a field.
            FocusManager.GotFocus += (_, e) => HintBar.SetAcceptVerb(AcceptVerbFor(e.NewFocusedElement));

            // Connection events arrive on a polling thread, hence the marshal — the router says so.
            _input.PadFamilyChanged += family =>
                Post(() => HintBar.SetPadFamily(family));

            _input.PadAttachedChanged += attached =>
                Post(() => HintBar.SetPadAttached(attached));
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
                Post(
                    DispatcherQueuePriority.Low,
                    () => SeedFocusIfNothingHasIt());
            }
        };

        // Closing mid-stream has to end the session before the process goes.
        //
        // The session page's teardown is deliberately not awaited on the UI thread — doing that can freeze the
        // window on exit — so on an ordinary "leave the stream" it simply completes while the window
        // lives on. At shutdown there is no window to live on: the task is dropped, the process exits, and the
        // teardown's last acts never happen. Those acts are the control-channel close and, on the account
        // route, an HTTPS call leaving the cloud session, which is what tells PSN and the console we are gone.
        // Without it the console can be left holding a session nobody is in.
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;

        Closed += (_, _) =>
        {
            AppWindow.Changed -= OnAppWindowChanged;

            // First, so no focus check queued before the close runs against the closed window. Such a check reads
            // Window.Content from a released native window and faults with an access violation, which no catch
            // can stop.
            _focusWatchdog.Dispose();
            _closed = true;
            _focusTrace?.Dispose();

            AppEffects.Changed -= OnEffectsChanged;
            _input.IntentReceived -= OnNavIntent;
            _input.Dispose();
        };
    }

    // ---- stream layer ----

    /// <summary>True while the stream layer is showing.</summary>
    public bool IsStreaming => StreamFrame.Visibility == Visibility.Visible;

    /// <summary>
    /// Tell the session page how much of the bottom of the window the button prompts cover, 0 while they are
    /// hidden. The prompts sit over the stream layer rather than in a row of their own (see MainWindow.xaml), and
    /// the page lifts its bottom band clear of them. Pushed from here, as OnReturnedFromStream is, because the
    /// prompts belong to the window. Both of the bar's edges call it, since a bar going away collapses without
    /// reliably reporting a size change.
    /// </summary>
    private void PushBottomInset()
    {
        double inset = HintBar.Visibility == Visibility.Visible ? HintBar.ActualHeight : 0;

        if (StreamFrame.Content is SessionPage page)
        {
            page.SetBottomInset(inset);
        }

        // The chrome pages too, which the prompts cover the same way: without the margin "Add another console"
        // sits under them. The page's bottom moves up while the prompts show; that beats a control nobody can
        // see.
        ChromeFrame.Margin = new Thickness(0, 0, 0, inset);
    }

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
        PushBottomInset();

        // Drop the backdrop for the duration. The window is about to be covered by opaque video, so Mica would
        // be blurring the wallpaper behind it every frame for something nobody can see — invisible on screen,
        // measurable in power, and this app's whole point is running on a handheld.
        SystemBackdrop = null;

        // Hide the chrome underneath so nothing renders behind the video.
        ChromeFrame.Visibility = Visibility.Collapsed;
        ApplyStreamChrome();
    }

    /// <summary>
    /// Set once the close has been allowed through, so the second <c>Close()</c> is not intercepted again.
    /// </summary>
    private bool _closeAllowed;

    /// <summary>Set in <c>Closed</c>. Work posted before the close finds it set when it runs.</summary>
    private bool _closed;

    /// <summary>
    /// Traces the whole window's focus moves, on ordinary pages as well as in dialogs, when
    /// <c>RIPCORD_TRACE_FOCUS</c> asks for them.
    /// </summary>
    private readonly FocusTrace? _focusTrace = FocusTrace.StartIfEnabled("shell");

    /// <summary>
    /// Queue work for the UI thread, to be dropped if the window has closed by the time it runs.
    ///
    /// <para>
    /// Every callback this window queues goes through here, because the dispatcher drains its queue while the
    /// app shuts down, after the window has closed. Queued work that touches the window then throws (a focus
    /// seed reading <c>ChromeFrame.Content</c> threw <c>E_UNEXPECTED</c>; dump of 2026-09-30, closing the app),
    /// and an exception out of a dispatcher callback is a fail-fast.
    /// </para>
    ///
    /// <para>
    /// The check is made when the work runs, not when it is queued.
    /// </para>
    /// </summary>
    private bool Post(DispatcherQueueHandler work) => Post(DispatcherQueuePriority.Normal, work);

    private bool Post(DispatcherQueuePriority priority, DispatcherQueueHandler work)
        => _dispatcherQueue.TryEnqueue(priority, () =>
        {
            if (!_closed)
            {
                work();
            }
        });

    /// <summary>
    /// In full screen, keep the content flush with the top of the display (<see cref="ContentBridge"/>). Posted at
    /// low priority because the presenter change sizes the window after this event, and the content host is
    /// placed one pixel down as part of that; pinning here would be undone at once. Again on every size change
    /// while full screen, since a display or scale change places the host afresh.
    /// </summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Leaving full screen hands the window a fresh overlapped presenter, which knows nothing of the minimum.
        if (args.DidPresenterChange)
        {
            ApplyMinimumSize();

            // Back from full screen, where the caption strip was cleared: the title bar sets its own again.
            if (!IsFullScreen)
            {
                Post(DispatcherQueuePriority.Low, () => SetTitleBar(AppTitleBar));
            }
        }

        if ((args.DidPresenterChange || args.DidSizeChange) && IsFullScreen)
        {
            Post(DispatcherQueuePriority.Low, () =>
            {
                if (IsFullScreen)
                {
                    ContentBridge.PinToClientArea(WinRT.Interop.WindowNative.GetWindowHandle(this));
                    ClearCaptionRegion();
                }
            });
        }
    }

    /// <summary>
    /// The window's icon, which is also the taskbar's for the zip build: the tile normally, and the one-colour
    /// mark under a contrast theme, white on the dark ones and black on the light one, the way Windows' inbox apps
    /// change theirs. The packaged build's Start and taskbar tiles switch by themselves through
    /// their contrast-qualified assets; the zip build has no resource system, so it is done here, and again when
    /// contrast is turned on or off while the app is open.
    /// </summary>
    private void ApplyAppIcon()
    {
        string icon = "Assets/AppIcon.ico";

        if (AppEffects.HighContrast)
        {
            Windows.UI.Color window = new Windows.UI.ViewManagement.UISettings()
                .UIElementColor(Windows.UI.ViewManagement.UIElementType.Window);
            double luminance = (0.299 * window.R) + (0.587 * window.G) + (0.114 * window.B);
            icon = luminance < 128 ? "Assets/AppIcon.contrast-black.ico" : "Assets/AppIcon.contrast-white.ico";
        }

        AppWindow.SetIcon(icon);
    }

    /// <summary>
    /// What the accept button does on <paramref name="focused"/>, for the hint bar, or null for the scope's own
    /// "Select". Only where "Select" undersells it (showcase plan, part 6): a console card says its own action,
    /// which is the difference between expecting a stream and expecting a wait.
    /// </summary>
    private static string? AcceptVerbFor(object? focused) => focused switch
    {
        GridViewItem { Content: ConsoleCardViewModel card } => card.State.PrimaryActionLabel,
        ToggleSwitch or CheckBox => ButtonLabels.VerbToggle,
        Microsoft.UI.Xaml.Controls.Primitives.ToggleButton and not RadioButton => ButtonLabels.VerbToggle,
        TextBox or PasswordBox or RichEditBox => ButtonLabels.VerbType,
        ComboBox => ButtonLabels.VerbOpen,
        _ => null,
    };

    /// <summary>
    /// Windows' own navigation sounds, while a pad is in use and never otherwise: the convention the Xbox shell
    /// set, where moving and pressing click and a mouse stays silent (showcase plan, decision D).
    /// ElementSoundPlayer plays only for its own controls' gestures, so FocusPilot plays the moves and
    /// presses the pad makes through it. Settings can turn them off.
    /// </summary>
    private void UseNavigationSounds(InputMode mode)
        => ElementSoundPlayer.State = mode == InputMode.Controller && _settingsStore.Current.NavigationSounds
            ? ElementSoundPlayerState.On
            : ElementSoundPlayerState.Off;

    /// <summary>
    /// The smallest the window may be made, in effective pixels. Below it the console card's play mark is cut
    /// off, the one thing the home page exists for. 640 is also where the console grid
    /// drops to one column (<c>CardMetrics.SingleColumnWidth</c>), so the two rules meet.
    /// </summary>
    private const double MinimumWidth = 640;

    /// <inheritdoc cref="MinimumWidth"/>
    private const double MinimumHeight = 480;

    /// <summary>
    /// Hold the window to <see cref="MinimumWidth"/> × <see cref="MinimumHeight"/>. The presenter takes physical
    /// pixels, so this runs again whenever the scale changes, and whenever the presenter is replaced.
    /// </summary>
    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter
            || (Content as FrameworkElement)?.XamlRoot is not { } root)
        {
            return;
        }

        double scale = root.RasterizationScale;
        presenter.PreferredMinimumWidth = (int)Math.Ceiling(MinimumWidth * scale);
        presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinimumHeight * scale);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Nothing streaming, or we are already on the way out: let it close.
        if (_closeAllowed || StreamFrame.Content is not SessionPage page)
        {
            return;
        }

        args.Cancel = true;
        _ = CloseWhenSessionHasEndedAsync(page);
    }

    /// <summary>
    /// End the session, then close for real.
    ///
    /// <para>
    /// <b>Bounded, because a hang here is worse than an unclean exit.</b> The teardown's own cloud call carries
    /// a three-second timeout, so four is enough for the whole of it and still short enough that a user who
    /// clicked the X is not left wondering. If it overruns we close anyway, rather than leave a window that
    /// will not shut.
    /// </para>
    /// </summary>
    private async Task CloseWhenSessionHasEndedAsync(SessionPage page)
    {
        try
        {
            // Unloads the page, which is what starts the teardown and sets its task.
            CloseStream();

            if (page.Teardown is { } teardown)
            {
                await Task.WhenAny(teardown, Task.Delay(TimeSpan.FromSeconds(4)));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Ripcord] session teardown on close failed: {ex}");
        }
        finally
        {
            _closeAllowed = true;
            Close();
        }
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
        TitleBarCommands.Visibility = Visibility.Visible;
        AppTitleBar.IsBackButtonVisible = ChromeFrame.CanGoBack;

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
    /// Colour the minimise, maximise and close buttons for the theme the window is actually in.
    ///
    /// <para>
    /// Left to the platform, turning High Contrast off restores the page but not the caption buttons, which
    /// keep a light background in a dark window until relaunch. So they are painted
    /// here on every theme or contrast change. In High Contrast every colour is cleared, which hands the
    /// buttons back to the system's contrast theme.
    /// </para>
    ///
    /// <para>
    /// The values are Windows 11's own caption-button fills: transparent at rest, the subtle fill on hover,
    /// a fainter one pressed, the primary text colour for the glyphs and the disabled text colour when the
    /// window is inactive.
    /// </para>
    /// </summary>
    private void PaintCaptionButtons()
    {
        AppWindowTitleBar bar = AppWindow.TitleBar;

        if (AppEffects.HighContrast)
        {
            bar.ButtonBackgroundColor = null;
            bar.ButtonInactiveBackgroundColor = null;
            bar.ButtonForegroundColor = null;
            bar.ButtonInactiveForegroundColor = null;
            bar.ButtonHoverBackgroundColor = null;
            bar.ButtonHoverForegroundColor = null;
            bar.ButtonPressedBackgroundColor = null;
            bar.ButtonPressedForegroundColor = null;
            return;
        }

        bool dark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        Windows.UI.Color glyph = dark ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xE4, 0x00, 0x00, 0x00);
        Windows.UI.Color inactive = dark ? Windows.UI.Color.FromArgb(0x5D, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x5C, 0x00, 0x00, 0x00);

        bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = glyph;
        bar.ButtonInactiveForegroundColor = inactive;
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x09, 0x00, 0x00, 0x00);
        bar.ButtonHoverForegroundColor = glyph;
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x06, 0x00, 0x00, 0x00);
        bar.ButtonPressedForegroundColor = glyph;
    }

    /// <summary>
    /// Transparency or high contrast changed while running: repaint the caption buttons, pick the icon again and,
    /// outside a stream, re-decide the backdrop. The accent wash and the family marks resolve through
    /// <c>AccentResources</c> on each binding pass, so they follow on the next render without anything being told.
    /// </summary>
    private void OnEffectsChanged()
    {
        PaintCaptionButtons();
        ApplyAppIcon();

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

        // The title bar itself stays in windowed mode — it carries the window controls and the drag region, and
        // taking those away would trap the user in a window they cannot move or close. Its COMMANDS go, because
        // they navigate the chrome frame, which is collapsed behind opaque video: left up they are visible,
        // clickable and completely inert, which is worse than absent.
        TitleBarCommands.Visibility = Visibility.Collapsed;
        AppTitleBar.IsBackButtonVisible = false;

        Grid.SetRow(StreamFrame, fullScreen ? 0 : 1);
        Grid.SetRowSpan(StreamFrame, fullScreen ? 2 : 1);
    }

    /// <summary>
    /// Full screen has no title bar, but the window keeps a caption strip across its top, so a press there moves the
    /// window: the stream's diagnostics panel sits in it, and dragging it by its header would drag Ripcord instead.
    /// Cleared while full screen; the title bar sets its own again when it is back.
    /// </summary>
    private void ClearCaptionRegion()
    {
        try
        {
            Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption, []);
        }
        catch (Exception)
        {
            // Only a drag that lands on the window instead; nothing to recover.
        }
    }

    private void TitleBar_BackRequested(TitleBar sender, object args) => GoBack();

    /// <summary>
    /// A plain arrow key nothing else used moves focus through <see cref="FocusPilot"/>, the pad's path, rather
    /// than WinUI's own arrow-key navigation.
    ///
    /// <para>
    /// WinUI's search (the root grid's <c>XYFocusKeyboardNavigation</c>) gives up on "Add another console" once
    /// focus has come from the title bar, and the pad's straight-line fallback lives in FocusPilot, so on WinUI's
    /// path the arrow keys stick where the pad does not. Routing them here makes the two one path.
    /// A key only arrives here unhandled, so text boxes, sliders and the GridView's own item navigation keep
    /// their arrows; a dialog's keys never reach the window's root.
    /// </para>
    /// </summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        NavDirection direction = e.Key switch
        {
            Windows.System.VirtualKey.Up => NavDirection.Up,
            Windows.System.VirtualKey.Down => NavDirection.Down,
            Windows.System.VirtualKey.Left => NavDirection.Left,
            Windows.System.VirtualKey.Right => NavDirection.Right,
            _ => NavDirection.None,
        };

        if (direction == NavDirection.None || _closed || ModalOwnsFocus || ModifierHeld())
        {
            return;
        }

        _focus.MoveFocus(direction);
        e.Handled = true;
    }

    /// <summary>Alt, Ctrl or Shift is down, so the arrow belongs to a shortcut such as Alt+Left.</summary>
    private static bool ModifierHeld()
        => IsDown(Windows.System.VirtualKey.Menu)
            || IsDown(Windows.System.VirtualKey.Control)
            || IsDown(Windows.System.VirtualKey.Shift);

    private static bool IsDown(Windows.System.VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Window-level keyboard shortcuts.
    ///
    /// <para>
    /// In code rather than markup because <c>VirtualKey</c> has no name for the comma, so <c>Ctrl+,</c> — the
    /// shortcut every Windows app uses for settings — can only be written as its numeric code; the other
    /// accelerators sit beside it rather than split across two files.
    /// </para>
    ///
    /// <para>
    /// Deliberately no <c>TabIndex</c> anywhere in this app: tab order follows declaration order. Where the
    /// order is wrong, the markup moves.
    /// </para>
    /// </summary>
    private void AddAccelerators()
    {
        if (Content is not UIElement root)
        {
            return;
        }

        // No tooltip for these. WinUI shows an element's first accelerator as a tooltip by default, and on the
        // window's root that is every pixel of the window: "Alt+Left" would come up wherever the pointer rests.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        root.KeyDown += OnRootKeyDown;

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

        // Escape goes back. Only outside a stream: in a session Escape belongs to the session (leave the
        // stream), and that reservation is why it is not a bindable key.
        //
        // Declining rather than doing nothing is the whole point here. This accelerator sits on the window
        // root and therefore sees Escape before the session page's own does, so swallowing it while streaming
        // would leave Escape dead in a session.
        AddIf(Windows.System.VirtualKey.Escape, Windows.System.VirtualKeyModifiers.None, () =>
        {
            if (Shell().IsStreaming)
            {
                return false; // the session's Escape, not ours
            }

            GoBack();
            return true;
        });

        // F6 cycles between the window's two focus regions — the title-bar commands and the page. There are
        // only two, which is exactly what makes F6 worth having: there is no other key that reaches the title
        // bar.
        Add(Windows.System.VirtualKey.F6, Windows.System.VirtualKeyModifiers.None, CycleRegion);

        // Two shapes on purpose. Most of these always act, and say so by taking an Action. One does not — and
        // an accelerator that may decline has to report that, because marking the event handled is what stops
        // it reaching anything else. Doing it unconditionally and then deciding inside is the bug this shape
        // exists to make unrepresentable: the key is consumed either way, and a handler further in never runs.
        void Add(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Action invoke)
            => AddIf(key, modifiers, () =>
            {
                invoke();
                return true;
            });

        void AddIf(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Func<bool> invoke)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };

            accelerator.Invoked += (_, args) => args.Handled = invoke();

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

    /// <summary>
    /// The consoles, or the first-run setup when this install hasn't finished one (SetupFlow.ScopeFor). Not when
    /// the app was started to play a console (a shortcut or the jump list): that asked to play, and the setup can
    /// wait for the next ordinary start.
    /// </summary>
    private void NavigateToFirstPage()
    {
        SetupScope? setup = null;
        if (App.Launch.Action == LaunchAction.Shell)
        {
            int paired;
            try
            {
                paired = App.Services.Consoles.Load().Count;
            }
            catch (Exception)
            {
                paired = 0;   // the consoles page says why the store can't be read
            }

            setup = SetupFlow.ScopeFor(App.Services.Settings.Current, paired);
        }

        if (setup is { } scope)
        {
            ChromeFrame.Navigate(typeof(SetupPage), scope);
        }
        else
        {
            ChromeFrame.Navigate(typeof(ConsolesPage));
        }
    }

    /// <summary>
    /// The single back route. Every gesture that means "back" — the title-bar chevron, Alt+Left, Escape and the
    /// pad's East button outside a stream — comes through here, so they cannot disagree about what back does.
    /// </summary>
    private void GoBack()
    {
        // Closing what is on top comes first. Otherwise Back with a context menu open navigates the page
        // underneath it, which is both surprising and leaves the menu on screen.
        if (_focus.TryDismissPopup())
        {
            return;
        }

        // Then a page's own steps, so Back on the setup's third step goes to its second rather than out.
        if (ChromeFrame.Content is IStepBack steps && steps.TryStepBack())
        {
            return;
        }

        if (ChromeFrame.CanGoBack)
        {
            ElementSoundPlayer.Play(ElementSoundKind.GoBack);
            ChromeFrame.GoBack();
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => NavigateToUtility(typeof(SettingsPage));

    private void OnAboutClick(object sender, RoutedEventArgs e) => NavigateToUtility(typeof(AboutPage));

    /// <summary>
    /// <see cref="IShellNavigator.ShowSettings"/>. The same route the gear button takes, carrying where the
    /// caller wanted somebody to end up.
    /// </summary>
    public void ShowSettings(SettingsDestination destination = SettingsDestination.Top)
        => NavigateToUtility(typeof(SettingsPage), destination);

    /// <summary>
    /// Open one of the two utilities.
    ///
    /// <para>
    /// Navigation rather than a pane selection: opening Settings pushes onto the back stack, so the chevron and
    /// B both return you to what you were doing.
    /// </para>
    ///
    /// <para>
    /// Re-entry without a parameter is ignored so that pressing the same command twice does not stack duplicates
    /// behind you.
    /// </para>
    /// </summary>
    private void NavigateToUtility(Type pageType, object? parameter = null)
    {
        // Re-navigated when a parameter is carried, even if the page is already up: "take me to the account"
        // has to be honoured by a page that happens to be showing already, or the second press does nothing.
        if (ChromeFrame.Content?.GetType() != pageType || parameter is not null)
        {
            ChromeFrame.Navigate(pageType, parameter);
        }
    }

    /// <summary>
    /// Act on one navigation intent. Reached only while a non-Session scope owns the pad — the router does
    /// that arbitration, so this does not have to know what is in the stream layer or ask a page for a bool.
    /// </summary>
    private void OnNavIntent(NavIntent intent)
    {
        Post(() =>
        {
            // A background input-polling handler must never be able to take the whole process down.
            try
            {
                // Reported here rather than from the router's frame handler so the tracker stays on one
                // thread — and an intent is precisely the "deliberate activity" its contract asks for.
                _input.ReportControllerActivity();

                // Nothing focused: directional movement is relative to whatever has focus, so this press first
                // seeds the caret.
                //
                // Seeding does not swallow the press outright: anything that disturbs focus (a tooltip appearing
                // under the mouse is enough) would then cost two presses for one action.
                //
                // A DIRECTION after seeding is safe and is what was asked for, so it runs. An ACTIVATION is
                // not: pressing Select would act on whatever the seed happened to land on, which the user has
                // not seen. So those, and only those, cost a press.
                if (SeedFocusIfNothingHasIt() && (intent.Accept || intent.Context || intent.Back))
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
                    // On the stream layer Back leaves it, as its own "Back to consoles" does; going back in the
                    // collapsed chrome frame underneath does nothing visible. A pad only gets here while no stream
                    // is live (the session scope keeps its presses), so this is a connect, or a connect that failed.
                    //
                    // Or a prompt over a live stream, which is why a dialog closes first: the disconnect prompt
                    // is the stream asking to leave, and asking to leave again is ignored by the page while it
                    // waits for the answer, so B has to close the prompt itself.
                    if (_focus.TryDismissPopup())
                    {
                        // Closed what was on top.
                    }
                    else if (IsStreaming && StreamFrame.Content is SessionPage page)
                    {
                        page.Leave();
                    }
                    else
                    {
                        GoBack();
                    }
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

        // Returns what actually happened, so a keyboard that did not open — for any reason, an exception
        // included — is not reported to the caller as a handled press.
        return _softKeyboard.TryOpen(target);
    }

    /// <summary>
    /// True while a modal owns focus, in which case the shell must keep its hands off.
    ///
    /// <para>
    /// The PSN sign-in dialog hosts a WebView2, which is a native child HWND. Click into an HTML text box and
    /// Win32 focus goes there, but XAML focus goes nowhere: <c>FocusManager.GetFocusedElement</c> returns null,
    /// <see cref="FocusPilot.NeedsFocusSeed"/> reads that as "nothing has focus", and the shell would put focus
    /// back into the dialog, deselecting the password box the instant it was clicked.
    /// </para>
    ///
    /// <para>
    /// The guard covers every modal, not only the web view: <see cref="FocusFirstContentElement"/> focuses the
    /// page inside <c>ChromeFrame</c>, which while a modal is up is the content BEHIND it, and that is never
    /// right. A modal keeps focus for as long as it is showing: <c>ContentDialog</c> seeds and traps its own.
    /// </para>
    /// </summary>
    private bool ModalOwnsFocus => _input.Scopes.Top?.Kind == InputScopeKind.Modal;

    /// <summary>
    /// Whether the shell should put focus into the page — the question every seeding path has to ask, the focus
    /// watchdog included: <see cref="FocusPilot.NeedsFocusSeed"/> on its own has no <see cref="ModalOwnsFocus"/>
    /// guard, and without one the sign-in box is unusable.
    /// </summary>
    private bool ShellShouldSeedFocus() => !ModalOwnsFocus && _focus.NeedsFocusSeed();

    /// <summary>
    /// Put focus somewhere if it is nowhere. Returns true when it had to act, so a caller can treat the press
    /// that prompted it as spent — which is also why the modal case returns false rather than swallowing the
    /// press: nothing moved, so nothing was spent.
    /// </summary>
    private bool SeedFocusIfNothingHasIt()
    {
        if (!ShellShouldSeedFocus())
        {
            return false;
        }

        FocusFirstContentElement();
        return true;
    }

    /// <summary>
    /// Seed focus inside the page content.
    ///
    /// <para>
    /// Content, not chrome: the first thing focused is the first thing on the page — which on the console list
    /// is a console — so a player with a pad in hand does not have to travel out of the furniture first.
    /// </para>
    /// </summary>
    private void FocusFirstContentElement()
    {
        // The chokepoint. Every other seeding path leads here — window activation, navigation, chrome scope
        // activation, a pad direction with nothing focused, the watchdog, region cycling — so guarding them
        // one at a time is how one gets missed.
        // Focusing the page behind a modal is wrong in all of them. Nor is there a page to focus once the
        // window has closed: ChromeFrame.Content throws then.
        if (_closed || ModalOwnsFocus)
        {
            return;
        }

        // The layer on screen. While the stream layer is up the chrome frame is collapsed, and seeding into it
        // focuses nothing: on a connect that failed, every press would be spent seeding and none would reach
        // Try again.
        if ((IsStreaming ? StreamFrame.Content : ChromeFrame.Content) is not FrameworkElement content)
        {
            return;
        }

        // FocusState.Keyboard everywhere below, never Programmatic — see the note in MoveFocus. Seeding focus
        // programmatically draws no focus visual, so the app's initial focus would be invisible.

        // The page's own answer first: it knows which element is the point of the page, and tree order does
        // not. On the console list that is a console rather than the "Add console" button above it.
        // Focus() reports whether it landed. A GridView that has not realised any items yet accepts the call
        // and focuses nothing, which would otherwise leave the app with no focus at all — worse than the
        // fallback, because directional input needs an anchor to move from.
        if (content is IInitialFocusTarget target
            && target.InitialFocus is { } preferred
            && preferred.Focus(FocusState.Keyboard))
        {
            RecheckArrival(content);
            return;
        }

        // Descend past containers. FindFirstFocusableElement happily returns a ScrollViewer — one is focusable
        // whenever it can scroll — and focusing a container is a TRAP, not merely untidy: directional search
        // starts from the focused element, and a container that ENCLOSES every candidate has nothing above,
        // below or beside it, so every direction finds nothing and focus can never leave. The settings page is
        // one whose first focusable element is such a container.
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
                _seedRetries = 0;
                RecheckArrival(content);
                return;
            }

            if (scroller.Content is not DependencyObject inner)
            {
                break;
            }

            scope = inner;
        }

        // Nothing focusable on the page: it may still be populating. Ask again shortly, for up to half a
        // second, before settling for the chrome. Without the wait a page that fills itself in after navigating -
        // Keyboard controls builds its rows that way - opens every time with focus on the title-bar gear, so a
        // pad user starts in the window's furniture instead of the first row.
        if (_seedRetries < SeedRetryLimit)
        {
            _seedRetries++;
            _seedRetry ??= CreateSeedRetry();
            _seedRetry.Start();
            return;
        }

        // The title-bar commands are always there, so focus is at least somewhere a pad can move from.
        _seedRetries = 0;
        _arrivalSeedPending = false;
        SettingsButton.Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// A page change is under way and focus should end up in the new page. Cleared once it has, after the one
    /// layout pass that can still move it.
    /// </summary>
    private bool _arrivalSeedPending;

    /// <summary>
    /// Whether focus, just after a page change, has ended up outside the new page: on the title bar, where Windows
    /// puts it when the control that held it leaves with the old page.
    ///
    /// <para>
    /// That fallback can come after the seed, and a seeding path that takes any focused control as placed leaves
    /// Keyboard controls open on the title bar's Back, or About on the About button: one press of A from a pad
    /// and the player goes back, or nowhere.
    /// </para>
    /// </summary>
    private bool ArrivalFocusStrayed()
    {
        if (!_arrivalSeedPending || ModalOwnsFocus || _closed || IsStreaming || ChromeFrame.Content is not DependencyObject page
            || Content?.XamlRoot is not { } root)
        {
            return false;
        }

        for (var node = FocusManager.GetFocusedElement(root) as DependencyObject; node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, page))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// After an arrival seed lands, look once more when the next layout has run, and seed again if focus has
    /// strayed out of the page. One layout pass is too short for a person to have moved it themselves.
    /// </summary>
    private void RecheckArrival(FrameworkElement content)
    {
        if (!_arrivalSeedPending)
        {
            return;
        }

        void OnLayout(object? sender, object e)
        {
            content.LayoutUpdated -= OnLayout;
            Post(DispatcherQueuePriority.Low, () =>
            {
                bool strayed = ArrivalFocusStrayed();
                _arrivalSeedPending = false;
                if (strayed)
                {
                    FocusFirstContentElement();
                }
            });
        }

        content.LayoutUpdated += OnLayout;
    }

    private const int SeedRetryLimit = 10;
    private int _seedRetries;
    private DispatcherQueueTimer? _seedRetry;

    private DispatcherQueueTimer CreateSeedRetry()
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.IsRepeating = false;

        // Only if nothing has claimed focus in the meantime (the user may already have moved), and never behind a
        // modal: ShellShouldSeedFocus is the guarded question every seeding path asks.
        timer.Tick += (_, _) =>
        {
            if (ShellShouldSeedFocus() || ArrivalFocusStrayed())
            {
                FocusFirstContentElement();
            }
            else
            {
                _seedRetries = 0;
            }
        };

        return timer;
    }

}
