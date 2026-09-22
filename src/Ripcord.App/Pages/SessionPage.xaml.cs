using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Ripcord.Client;
using Ripcord.Core.Consoles;
using Ripcord.Core.Input;
using Ripcord.Core.Platform;
using Ripcord.Core.Power;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Diagnostics;
using Ripcord.Input;
using Ripcord.Media;
using Ripcord.Presentation;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Sessions;
using Ripcord.Core.Security;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Discovery;
using Ripcord.Protocol.Halyard.Session;
using Ripcord_App.Accents;
using Ripcord_App.Dialogs;
using Ripcord_App.Input;
using Ripcord_App.Converters;
using Ripcord_App.Services;
using WinRT;
using Ripcord.Core.Reactive;

namespace Ripcord_App.Pages;

/// <summary>
/// The streaming surface. Its job is now presentation only: <see cref="SessionController"/> owns the session
/// lifecycle (connect, degrade, reconnect, tear down), so this page renders status, routes controller input,
/// and handles the immersive-mode concerns a Page is actually responsible for.
/// </summary>
public sealed partial class SessionPage : Page, IVideoPipelinePreparer
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly RipcordAppServices _services = App.Services;
    private readonly ISettingsStore _settingsStore = App.Services.Settings;

    private RipcordSettings _settings = new();

    // Pad frames and keyboard frames merged into the single stream the session consumes. Frames are absolute
    // state, so the two sources have to be combined rather than interleaved — see MergedInputSource.
    private MergedInputSource? _inputSource;

    // The element the key handlers are attached to: the window root, so delivery does not depend on which element
    // holds focus. Held so it can be unsubscribed from exactly what was subscribed to.
    private UIElement? _keyRoot;

    // On-screen controls: idle timer plus which console buttons the user is currently holding, so the bar never
    // hides out from under a finger.
    private DispatcherTimer? _touchControlsTimer;
    private ControllerButtons _virtualButtonsHeld;

    /// <summary>How long the on-screen controls linger after the last pointer activity.</summary>
    private static readonly TimeSpan TouchControlsIdleTimeout = TimeSpan.FromSeconds(4);
    private IDisposable? _connectionsSubscription;

    private D3D12VideoDecodePipeline? _pipeline;
    private SessionController? _controller;
    private IDisposable? _statusSubscription;
    private DispatcherTimer? _statsTimer;

    // What this surface SAYS, as opposed to what it draws. Everything the overlay and the diagnostics readout
    // report is composed in SessionViewModel, where it is unit-tested against neither a GPU nor a console.
    private readonly D3D12VideoPipelineStats _pipelineStats = new();
    private readonly SessionViewModel _viewModel;

    // What Render last applied, so the capability pills — the one part that builds elements rather than setting
    // text — are rebuilt only when the SET changes and not twice a second.
    private string _renderedPillSignature = string.Empty;

    private string _renderedSummaryPillSignature = string.Empty;

    private double _appliedDiagnosticsInset = 16;

    private StreamWriter? _trace;

    private string? _tracePath;

    private int _traceRowsSinceFlush;

    /// <summary>
    /// False until the preamble has been written. The preamble carries the GPU name, and the GPU is not known
    /// at connect time - the adapter is resolved when the decode pipeline initialises, which is the first
    /// frame, not the first tick. Writing the preamble eagerly produced an empty <c>adapter:</c> line in every
    /// trace taken so far.
    /// </summary>
    private bool _tracePreambleWritten;

    /// <summary>
    /// Rows seen while still waiting for the adapter name. Bounded so a session that never decodes a frame
    /// still produces a file with a header rather than an unreadable list of bare numbers.
    /// </summary>
    private int _tracePreambleWaits;

    /// <summary>Which arrangement the panel is currently in, so the rebuild only runs when it changes.</summary>
    private bool _diagnosticsIsSheet;

    private PairedConsole? _console;
    private IPowerThermalMonitor? _powerMonitor;
    private ExitGestureDetector? _exitDetector;

    /// <summary>
    /// This page's claim on the pad. While it is on top the router stops producing navigation intents, so the
    /// chrome cannot consume the same buttons the console is being sent — which is what used to require the
    /// window to ask this page for a bool on every frame.
    ///
    /// <para>
    /// Its deactivation edge is the mid-session-modal fix in miniature: something pushed over this scope makes
    /// the session release what is physically held, so the combination that opened a dialog does not stay down
    /// inside the game underneath.
    /// </para>
    /// </summary>
    private ShellInputScope? _sessionScope;
    private bool _leaving;

    // True while the disconnect confirmation dialog is open. Guards the await gap in LeaveSession so a second
    // exit trigger (another Esc, the exit gesture) cannot stack a second dialog on top of the first.
    private bool _confirmingLeave;

    // Cancels the pre-connect work (currently the wake poll) when the user leaves before the session is up.
    private CancellationTokenSource? _connectCts;

    // Rate sampling, pill tracking, the attached-controller set, the rolling histories and every full-scale
    // constant used to live here. All of it is arithmetic and wording over plain numbers, so all of it moved to
    // SessionViewModel — see the histories and the FullScale members it exposes for the sparklines below.

    // Whether we switched the window to fullscreen, so we only restore what we changed.
    private bool _enteredFullScreen;

    public SessionPage()
    {
        // Resolved before InitializeComponent, as everywhere else, so bindings never see a null.
        _viewModel = _services.CreateSessionViewModel(_pipelineStats);

        InitializeComponent();

        _viewModel.PropertyChanged += (_, _) => Render(_viewModel.State);
        Render(_viewModel.State);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // The shelf the touch bar lives in. Sized here rather than in markup because the token is a double
        // and this is a GridLength; XAML will not convert, and binding it threw at page load - which is to
        // say when somebody opened a stream, not when anybody built.
        TouchShelfRow.Height = new GridLength(ThemeBrush.LookupDouble("RipcordTouchShelfHeight", fallback: 90));
        _console = e.Parameter as PairedConsole;
        ShowConnectIdentity();

        // Rung 1 names a route out of itself, and which route exists depends on what the player is holding.
        // The router's tracker already decides and debounces that; this layer takes the answer rather than
        // forming its own opinion from raw events. Seeded with the current mode because ModeChanged only
        // fires on a change, and someone who has been on a pad all evening would otherwise be told about a
        // key until they touched something.
        _viewModel.SetInputMode(App.Input.Mode);
        App.Input.ModeChanged += OnInputModeChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // The router outlives every page, so a page that stayed subscribed would be kept alive by it - and
        // would go on setting state on a view-model nobody is rendering.
        App.Input.ModeChanged -= OnInputModeChanged;
    }

    private void OnInputModeChanged(InputMode mode)
        => DispatcherQueue.TryEnqueue(() => _viewModel.SetInputMode(mode));

    /// <summary>
    /// On touch, the notice itself is the way deeper.
    ///
    /// <para>
    /// Only on touch. With a keyboard the pill names F3 and the notice is a label; with a pad there is no
    /// route at all and there is deliberately no pill. Making the notice tappable in every mode would give a
    /// mouse a target that says nothing about itself, which is how an invisible affordance gets built.
    /// </para>
    /// </summary>
    private void OnHealthAlertTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_viewModel.State.AlertHint != AlertHint.Tap)
        {
            return;
        }

        _viewModel.ToggleDiagnostics();
        e.Handled = true;
    }

    /// <summary>
    /// Land the console card the user pressed onto the connecting panel, so starting a stream carries the eye
    /// across instead of cutting to black.
    ///
    /// <para>
    /// Every failure here is silent and harmless: no prepared animation, an animation that has already
    /// expired, or a user who has turned animation effects off all end with the page appearing exactly as it
    /// did before this existed. Motion is the garnish, never the mechanism.
    /// </para>
    /// </summary>
    private void TryStartConnectAnimation()
    {
        if (!AppMotion.Enabled)
        {
            return;
        }

        try
        {
            ConnectedAnimation? animation = ConnectedAnimationService.GetForCurrentView()
                .GetAnimation(AppMotion.ConnectAnimationKey);
            animation?.TryStart(StatusOverlay);
        }
        catch (Exception)
        {
            // Decorative only.
        }
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        TryStartConnectAnimation();

        _settings = _settingsStore.Current;

        // Re-stated here rather than trusted from construction: the page is built when it is navigated to, and
        // the user may have changed a setting between then and the frame actually loading.
        _viewModel.UseSettings(_settings);

        _exitDetector = new ExitGestureDetector(_settings.ExitGesture);


        // Input set-up is ISOLATED and non-fatal. It reaches native code (GameInput via Ripcord.Input.Interop), and
        // a stream is perfectly watchable without a controller — so a failure here must degrade to "no input", never
        // prevent connecting. It previously ran unguarded ahead of the first status message, so any failure left the
        // static "Starting…" on screen with nothing reported anywhere.
        try
        {
            // Say when the keyboard is off. It is off by default and deliberately so, but a user pressing keys
            // at a stream and getting nothing has no way to tell that from a fault -- and this row is the one
            // place already telling them what input is running.
            InputEnginesText.Text = _settings.InputBindings.KeyboardEnabled
                ? $"engines: {App.Input.SourceName} + keyboard"
                : $"engines: {App.Input.SourceName} (keyboard off — enable it in Settings)";

            if (App.Input.Connections is { } connections)
            {
                _connectionsSubscription = connections.Subscribe(
                    new AnonymousObserver<ControllerConnectionEvent>(OnConnectionChanged));
            }

            App.Input.FrameReceived += OnPadFrame;

            // Keyboard support only. The gamepad remap is applied ONCE, by the router, before frames reach
            // here — so this must be constructed with an EMPTY remap. A remap is not idempotent: applying it
            // twice swaps a button and swaps it back, or chains A→B→C. If a bound button ever behaves as
            // though it were unbound, this is the line to look at.
            _inputSource = new MergedInputSource(
                pad: null,
                _settings.InputBindings with { GamepadRemap = new Dictionary<ControllerButtons, ControllerButtons>() });
        }
        catch (Exception ex)
        {
            _inputSource = null;
            ControllerConnectedText.Text = "unavailable";
            InputEnginesText.Text = $"input failed to start: {ex.Message}";
            Debug.WriteLine($"[Ripcord] input initialisation failed: {ex}");
        }

        // Keyboard routing, on the WINDOW ROOT rather than this page: bubbling key events start at the FOCUSED
        // element (so a focused button consumed Space), and SwapChainPanel is not focusable, so clicking the video
        // moves focus out of this page's subtree entirely. Subscribing at the root makes delivery focus-independent.
        WireConsoleButtons();

        // Show the controls once on entry so they are discoverable, then let them time out.
        ShowTouchControls();

        _keyRoot = App.MainWindow?.Content as UIElement ?? this;
        _keyRoot.PreviewKeyDown += OnPageKeyDown;
        _keyRoot.PreviewKeyUp += OnPageKeyUp;

        // Focus loss stops key-up delivery, so anything held at that moment would stay held indefinitely.
        if (App.MainWindow is { } window)
        {
            window.Activated += OnWindowActivated;
        }

        SizeChanged += Page_SizeChanged;
        ApplyDiagnosticsHeightLimit();

        FocusStreamSurface();

        // async void is confined to this one launcher, and it cannot throw: StartSessionAsync handles its own
        // failures and reports them through the status overlay.
        _ = StartSessionAsync();
    }

    private async Task StartSessionAsync()
    {
        // The previous attempt's last line must not be up while this one is deciding what to say. Covers
        // retry as well as a first connect, since retry comes back through here.
        _viewModel.ResetConnect();

        _connectCts = new CancellationTokenSource();

        // Diagnostics run from here on, before the session exists: an empty panel is least useful precisely
        // while something is failing to connect, and the GPU/adapter rows are already meaningful at this point.
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statsTimer.Tick += StatsTick;
        _statsTimer.Start();

        StartTrace();

        // A diagnostics row, not a gate. ConnectFlow independently refuses to connect without the constants;
        // this is the panel saying so, and it has to be written whether or not anyone opens the panel.
        if (_services.Sessions.Availability is { Available: false } unavailable)
        {
            D3D12StatusText.Text = $"Control constants NOT loaded — {unavailable.Detail}";
        }

        // The sequence itself is portable and tested off-device; this page supplies the one part that needs a
        // GPU (IVideoPipelinePreparer, implemented below) and renders each stage as it is announced.
        var flow = new ConnectFlow(_services.Sessions, _services.WakeCoordinator, this);
        var stages = new Progress<ConnectStage>(
            stage => _viewModel.ShowConnectStage(stage));

        ConnectPlan? plan = await flow.RunAsync(_console, _settings, stages, _connectCts.Token);
        if (plan is null)
        {
            // The flow reported a terminal stage saying why, or the page was left mid-connect.
            return;
        }

        // A real power monitor, so the adaptive controller's battery / energy-saver / critical-battery caps can
        // actually engage. Without one injected, SessionController falls back to UnknownPowerThermalMonitor,
        // which always claims external power — meaning a handheld on battery streamed at full desktop quality.
        _powerMonitor = PowerThermalMonitor.ForCurrentPlatform();

        // Keep whatever headline the flow last set and replace only the detail, so the console's own progress
        // lines land under "Connecting to your console…" instead of replacing it.
        var connectProgress = new Progress<string>(
            line => ShowStatus(_viewModel.State.StatusHeadline, line, terminal: false));

        // The controller owns everything from here: handshake, media/input routing, stall detection, reconnect.
        // The session it is handed owns whatever its route holds open, so teardown stays the controller's
        // ordinary dispose regardless of how the console was reached.
        _controller = new SessionController(
            token => _services.Sessions.OpenAsync(
                _console!, plan.Route, RequestLoginPinAsync, connectProgress, token),
            _pipeline!,
            _inputSource,
            _powerMonitor);

        _statusSubscription = _controller.Status.Subscribe(
            new AnonymousObserver<SessionStatus>(OnStatusChanged));

        OnStatusChanged(_controller.CurrentStatus);
        await _controller.StartAsync(plan.Config);
    }

    /// <summary>
    /// The session's login-passcode provider: show the PIN dialog on the UI thread and return the digits, or
    /// null if the user cancelled. Called from the session's connect thread, so it marshals onto the
    /// dispatcher; the session's own cancellation closes the dialog if the user leaves mid-prompt.
    /// </summary>
    private Task<string?> RequestLoginPinAsync(bool isRetry, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool queued = _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new LoginPinDialog(isRetry) { XamlRoot = XamlRoot };
                using CancellationTokenRegistration reg = cancellationToken.Register(() => dialog.Hide());
                ContentDialogResult result = await ModalHost.ShowAsync(dialog);
                tcs.TrySetResult(result == ContentDialogResult.Primary ? dialog.Pin : null);
            }
            catch (Exception)
            {
                // A dialog failure (e.g. no XamlRoot during teardown) must not hang the connect — treat as
                // cancelled so the session fails with its own clear "no passcode entered" message.
                tcs.TrySetResult(null);
            }
        });

        if (!queued)
        {
            tcs.TrySetResult(null);
        }

        return tcs.Task;
    }

    /// <summary>
    /// <see cref="IVideoPipelinePreparer"/>, implemented explicitly because it is a seam ConnectFlow calls,
    /// not part of the page's own surface. Implemented by the page rather than by an adapter class because
    /// the pipeline it creates is a page field with a page lifetime — an adapter would exist only to hold a
    /// reference back here.
    /// </summary>
    Task IVideoPipelinePreparer.PrepareAsync(SessionConfig config, CancellationToken cancellationToken)
        => InitVideoPipelineAsync(config);

    /// <summary>Stand up the D3D12 decode pipeline and bind its swap chain to the panel.</summary>
    private async Task InitVideoPipelineAsync(SessionConfig config)
    {
        // Adapter selection must be set before StartAsync (it decides which device to create); the upscale mode
        // is applied after, because its setter reaches into the renderer.
        _pipeline = new D3D12VideoDecodePipeline
        {
            GpuSelection = ToNativeGpuSelection(_settings.GpuPreference),
            SpecificAdapterLuid = _settings.GpuLuid,
        };

        _pipeline.DeviceLost += OnDeviceLost;
        await _pipeline.StartAsync(config, default);
        _pipeline.UpscaleMode = _settings.UpscaleMode;

        var panelNative = VideoPanel.As<ISwapChainPanelNative>();
        Marshal.ThrowExceptionForHR(panelNative.SetSwapChain(new IntPtr((long)_pipeline.SwapChainPointer)));

        // The view-model can read the pipeline from here on; before this, IsReady is false and the diagnostics
        // readout reports the "nothing yet" state rather than zeroes that look like real measurements.
        _pipelineStats.Attach(_pipeline);

        AdapterText.Text = $"GPU: {_pipeline.ActiveAdapterDescription}";

        // Keep the swap chain sized to the panel in physical pixels. These now only publish values for the
        // decode worker to pick up, so they never block this (UI) thread on GPU work.
        VideoPanel.SizeChanged += OnVideoPanelSizeChanged;
        VideoPanel.CompositionScaleChanged += OnVideoPanelScaleChanged;
        UpdateSwapChainSize();
    }

    private static Ripcord.Media.Interop.GpuSelection ToNativeGpuSelection(GpuPreference preference)
        => preference switch
        {
            GpuPreference.PreferEfficiency => Ripcord.Media.Interop.GpuSelection.PreferEfficiency,
            GpuPreference.PreferPerformance => Ripcord.Media.Interop.GpuSelection.PreferPerformance,
            GpuPreference.Specific => Ripcord.Media.Interop.GpuSelection.Specific,
            _ => Ripcord.Media.Interop.GpuSelection.Auto,
        };

    // ---- status ----

    /// <summary>
    /// A lifecycle change. What the overlay <em>says</em> about each one is the view-model's; what remains here
    /// is the device half — the presenter, keep-display-awake, and the exit hint.
    /// </summary>
    private void OnStatusChanged(SessionStatus status)
    {
        // Published from the controller's background loop, so marshal before touching XAML.
        _dispatcherQueue.TryEnqueue(() =>
        {
            _viewModel.ApplyLifecycle(status);

            // Who owns the pad follows the session being live. This is the edge that used to be a poll: the
            // window asked this page for a bool on every frame instead.
            UpdateSessionScope(status.IsLive);

            switch (status.Lifecycle)
            {
                case SessionLifecycle.Streaming:
                    EnterImmersiveMode();
                    _ = ShowExitHintBriefly();
                    break;

                case SessionLifecycle.Failed:
                    LeaveImmersiveMode();
                    break;
            }
        });
    }

    private void ShowStatus(string headline, string detail, bool terminal)
        => _viewModel.ShowStatus(headline, detail, terminal);

    private void HideStatus() => _viewModel.HideStatus();

    /// <summary>
    /// Take the connect composition off the picture, identity last.
    ///
    /// <para>
    /// Identity last because it is the element the <c>ConnectedAnimation</c> carried here from the card: the
    /// console's mark arrives first and leaves last, so the whole sequence reads as one object travelling
    /// rather than two screens swapping.
    /// </para>
    /// </summary>
    private void FadeOutStatusOverlay()
    {
        var fade = new Storyboard();

        var opacity = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(150)),
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(opacity, StatusOverlay);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        fade.Children.Add(opacity);

        // Collapsed only once it is invisible: leaving it Visible at zero opacity would keep it in the hit
        // test, and a transparent panel over a running game swallows the first click.
        fade.Completed += (_, _) =>
        {
            StatusOverlay.Visibility = Visibility.Collapsed;
            StatusOverlay.Opacity = 1;
        };

        fade.Begin();
    }

    /// <summary>
    /// Project the whole view-model state onto the controls. One method rather than per-property handlers,
    /// because the state arrives as one value and cannot be half-applied.
    /// </summary>
    private void Render(SessionViewState s)
    {
        // Faded rather than switched, on the way out only. The first decoded frame is the moment the player
        // has been waiting for, and a hard cut from the connect composition to the picture throws away the
        // one transition worth having. Opacity ONLY, which is exactly and only what Ripcord.Motion.xaml
        // permits on the video layer, and skipped entirely when motion is off.
        if (!s.StatusVisible && StatusOverlay.Visibility == Visibility.Visible && AppMotion.Enabled)
        {
            FadeOutStatusOverlay();
        }
        else
        {
            StatusOverlay.Opacity = 1;
            StatusOverlay.Visibility = Vis(s.StatusVisible);
        }
        StatusHeadline.Text = s.StatusHeadline;
        StatusDetail.Text = s.StatusDetail;
        RenderTrail(s);
        StatusActions.Visibility = Vis(s.StatusActionsVisible);
        ConnectEscape.Visibility = Vis(s.ConnectEscapeVisible);

        ControllerConnectedText.Text = s.ConnectedControllers;

        // Rung 1. Cheap enough to keep current unconditionally, unlike the panel below: it is two
        // properties, and it has to be right the instant it becomes visible.
        HealthAlert.Visibility = Vis(s.AlertVisible);
        if (s.AlertVisible)
        {
            // The NOTICE, not the bare verdict: a verdict plus one clause saying what to do about it. Rung 1
            // is the only rung most players will ever see, and "Losing packets on the network" on its own
            // names a problem and offers nothing.
            AlertText.Text = s.Diagnostics.HealthNotice;
            RenderHealthDot(AlertDot, s.Diagnostics.HealthLevel);

            // Named for the input actually in the player's hands. A pad gets neither pill: every route out
            // of rung 1 is one a pad cannot walk, and naming one would be worse than saying nothing.
            AlertKeyPill.Visibility = Vis(s.AlertHint == AlertHint.Key);
            AlertTapPill.Visibility = Vis(s.AlertHint == AlertHint.Tap);
        }

        // One source of truth for how much of the HUD is up. It used to be DiagnosticsPanel.Visibility,
        // consulted from eight places, three of which had to agree about a panel they did not own.
        // The decoded size is not known until the first frame, so placement cannot be settled at load.
        ApplyDiagnosticsPlacement();

        DiagnosticsSummary.Visibility = Vis(s.Rung == DiagnosticsRung.Summary);
        DiagnosticsPanel.Visibility = Vis(s.Rung == DiagnosticsRung.Full);

        if (s.Rung == DiagnosticsRung.Summary)
        {
            RenderSummary(s.Diagnostics);
        }

        // The state is kept current twice a second regardless; assigning two dozen text properties on a
        // collapsed panel is the part worth skipping.
        if (s.Rung == DiagnosticsRung.Full)
        {
            RenderDiagnostics(s.Diagnostics);
        }
    }

    /// <summary>
    /// Rung 2. Four numbers, and colour only where a reading has crossed its own threshold — the same grammar
    /// the sparklines use, for the same reason: a row of four coloured numbers would say nothing.
    /// </summary>
    private void RenderSummary(SessionDiagnosticsState d)
    {
        SummaryHealthText.Text = d.Health;
        SummaryTipText.Text = d.HealthTip;
        RenderHealthDot(SummaryDot, d.HealthLevel);

        SummaryResolutionText.Text = d.HeroResolution;
        SummaryFpsText.Text = d.HeroFps;
        SummaryBitrateText.Text = d.HeroBitrate;
        SummaryLossText.Text = d.HeroLoss;

        // Resolution and bitrate have no threshold of their own: the first is what was asked for and the
        // second is a magnitude. Only frames and loss can be wrong by themselves.
        ApplySeverity(SummaryFpsText, d.FramesSeverity);
        ApplySeverity(SummaryLossText, d.LossSeverity);

        RenderPills(SummaryPills, d, ref _renderedSummaryPillSignature);
    }

    /// <summary>Paint a value with its severity, or leave it in the default foreground when it is fine.</summary>
    private static void ApplySeverity(TextBlock value, MetricSeverity severity)
    {
        value.ClearValue(TextBlock.ForegroundProperty);

        if (severity != MetricSeverity.Normal && SeverityBrush(severity) is { } brush)
        {
            value.Foreground = brush;
        }
    }

    private void RenderDiagnostics(SessionDiagnosticsState d)
    {
        AdapterText.Text = d.Adapter;
        ColourText.Text = d.Colour;
        VideoFormatText.Text = d.VideoFormat;
        HdrOutputText.Text = d.HdrOutput;

        HeroResolutionText.Text = d.HeroResolution;
        HeroFpsText.Text = d.HeroFps;
        HeroBitrateText.Text = d.HeroBitrate;
        RenderCapabilityPills(d);
        DecoderText.Text = d.Decoder;

        RequestedText.Text = d.Requested;
        AdaptiveText.Text = d.Adaptive;
        AdaptiveLabel.Visibility = Vis(d.AdaptiveVisible);
        AdaptiveText.Visibility = AdaptiveLabel.Visibility;

        ReasonText.Text = d.Reason;
        ReasonLabel.Visibility = Vis(d.ReasonVisible);
        ReasonText.Visibility = ReasonLabel.Visibility;

        LinkText.Text = d.Link;
        HeadroomUsedColumn.Width = new GridLength(d.HeadroomUsedFraction, GridUnitType.Star);
        HeadroomFreeColumn.Width = new GridLength(1 - d.HeadroomUsedFraction, GridUnitType.Star);
        HeadroomText.Text = d.Headroom;
        PowerText.Text = d.Power;

        AudioText.Text = d.Audio;
        DecodeText.Text = d.Decode;
        QueuesText.Text = d.Queues;
        PathText.Text = d.Path;

        HealthText.Text = d.Health;
        HealthTipText.Text = d.HealthTip;
        RenderHealthDot(HealthDot, d.HealthLevel);
    }

    /// <summary>
    /// Rebuild the pill row, but only when the set has actually changed — this is the one part of the readout
    /// that creates elements rather than assigning text, and doing it twice a second churns layout for no
    /// visible difference. The view-model decides WHICH pills; this decides when redrawing is worth it.
    /// </summary>
    private void RenderCapabilityPills(SessionDiagnosticsState d)
        => RenderPills(CapabilityPills, d, ref _renderedPillSignature);

    /// <summary>
    /// Rebuild a pill row, but only when the set has actually changed - these are otherwise reconstructed
    /// twice a second for a row that changes about twice a session.
    ///
    /// <para>
    /// The cached signature belongs to the TARGET, not to the page. Two rungs show the same pills, and one
    /// shared cache would let whichever rendered first convince the other it was already up to date.
    /// </para>
    /// </summary>
    private static void RenderPills(ItemsControl target, SessionDiagnosticsState d, ref string cachedSignature)
    {
        if (d.CapabilityPillSignature == cachedSignature)
        {
            return;
        }

        cachedSignature = d.CapabilityPillSignature;
        target.Items.Clear();

        foreach (CapabilityPill pill in d.CapabilityPills)
        {
            target.Items.Add(new Border
            {
                // Application.Current.Resources, not this page's: the pill styles are shared now, and a page's
                // own dictionary does not see app-level ones.
                Style = (Style)Application.Current.Resources[
                    pill.Accent ? "RipcordAccentPillStyle" : "RipcordPillStyle"],
                Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock
                {
                    Text = pill.Label,
                    Style = (Style)Application.Current.Resources[
                        pill.Accent ? "RipcordAccentPillTextStyle" : "RipcordPillTextStyle"],
                },
            });
        }
    }

    /// <summary>
    /// The DOT carries the verdict's colour, not the headline: coloured body text fails contrast in some themes
    /// and reads as an error even when the verdict is "healthy". Theme brushes rather than hardcoded colours —
    /// the previous LimeGreen/Orange were wrong in light theme and invisible in high contrast — and looked up
    /// defensively, so a missing key can never crash the overlay.
    /// </summary>
    private void RenderHealthDot(Shape dot, StreamHealthLevel level)
    {
        string brushKey = level switch
        {
            StreamHealthLevel.Healthy => "SystemFillColorSuccessBrush",
            StreamHealthLevel.Warning => "SystemFillColorCautionBrush",
            StreamHealthLevel.Critical => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        };

        if (Application.Current.Resources.TryGetValue(brushKey, out object? brush) && brush is Brush themed)
        {
            dot.Fill = themed;
        }
    }

    /// <summary>
    /// A plotted metric's stroke. Neutral while the reading is inside its threshold, which is what makes a
    /// coloured line worth looking at; resolved defensively, like the health dot, so a missing key leaves the
    /// previous stroke rather than crashing the overlay or inventing a colour the design system does not own.
    /// </summary>
    private static Brush? SeverityBrush(MetricSeverity severity)
    {
        string key = severity switch
        {
            MetricSeverity.Warning => "SystemFillColorCautionBrush",
            MetricSeverity.Critical => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        };

        return Application.Current.Resources.TryGetValue(key, out object? brush) && brush is Brush themed
            ? themed
            : null;
    }

    /// <summary>
    /// Put the console's identity where its card's mark sat.
    ///
    /// <para>
    /// This is the whole of "connect is the card becoming the window rather than a new place you navigated
    /// to": the ConnectedAnimation lands the card's mark up here, and finding the same mark and the same name
    /// still on screen is what makes the transition read as continuous rather than as a jump.
    /// </para>
    /// </summary>
    private void ShowConnectIdentity()
    {
        if (_console is null)
        {
            ConnectIdentity.Visibility = Visibility.Collapsed;
            return;
        }

        ConsoleFamily family = ConsoleFamily.ForPlatformName(_console.Platform);
        ConnectMark.Accent = AccentResources.Brush(family.Accent);
        ConnectConsoleName.Text = _console.DisplayName;
        ConnectIdentity.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Light the trail as far as the connect has travelled.
    ///
    /// <para>
    /// A terminal stage keeps the phase it failed in and stops there rather than dimming to nothing: where it
    /// got to is the useful half of what went wrong, and a player who watched it stop at the second dash
    /// already knows the console never came up.
    /// </para>
    /// </summary>
    private void RenderTrail(SessionViewState s)
    {
        bool hasPhase = s.StatusVisible && s.Phase is not null;

        StatusTrail.Visibility = Vis(hasPhase);

        // Only when there is something in progress that the trail cannot describe. Both hidden at rest, so a
        // terminal state does not leave a bar cycling under a message saying it stopped.
        StatusBusyBar.Visibility = Vis(s.StatusVisible && s.Phase is null && s.StatusBusy);

        if (!hasPhase)
        {
            return;
        }

        int reached = s.Phase switch
        {
            ConnectPhase.Preparing => 1,
            ConnectPhase.Waking => 2,
            _ => 3,
        };

        PaintDash(TrailOne, reached >= 1);
        PaintDash(TrailTwo, reached >= 2);
        PaintDash(TrailThree, reached >= 3);
    }

    /// <summary>
    /// One dash of the trail. The vendor accent, because this is the console's own mark travelling — and it
    /// is resolved through AccentResources, so high contrast drops it to a system brush rather than painting
    /// decorative colour where the palette forbids it.
    /// </summary>
    private void PaintDash(Shape dash, bool lit)
    {
        ConsoleFamily family = ConsoleFamily.ForPlatformName(_console?.Platform);
        dash.Fill = AccentResources.Brush(family.Accent);
        dash.Opacity = lit ? 1.0 : 0.22;
    }

    /// <summary>Groups of the instrument panel, in the order they read. Column order in a sheet, row order otherwise.</summary>
    private IEnumerable<FrameworkElement> DiagnosticsGroups()
        => [DiagGroupStatus, DiagGroupTarget, DiagGroupMetrics, DiagGroupPipeline, DiagGroupDevice];

    /// <summary>
    /// Turn the panel on its side when it is sitting in a letterbox bar.
    ///
    /// <para>
    /// One set of facts in two arrangements, which is why this is a layout change and not a second panel: two
    /// copies of that markup would be two things to keep in step, and the one that is off screen is the one
    /// that would quietly stop matching.
    /// </para>
    ///
    /// <para>
    /// A bar is wide and short, so the column becomes a row and the panel stops being something you scroll.
    /// Everywhere else - the pillarbox rail, and the overlay when there is no dead space at all - it stays the
    /// tall narrow column it has always been.
    /// </para>
    /// </summary>
    private void ApplyDiagnosticsLayout(DiagnosticsPlacement placement)
    {
        bool sheet = placement == DiagnosticsPlacement.Sheet;
        if (sheet == _diagnosticsIsSheet)
        {
            return;
        }

        _diagnosticsIsSheet = sheet;

        DiagnosticsBody.RowDefinitions.Clear();
        DiagnosticsBody.ColumnDefinitions.Clear();

        int index = 0;
        foreach (FrameworkElement group in DiagnosticsGroups())
        {
            if (sheet)
            {
                // Star-sized rather than auto: the groups hold wildly different amounts of text, and letting
                // them size to content puts the sparklines in whatever width is left over.
                DiagnosticsBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(group, 0);
                Grid.SetColumn(group, index);
            }
            else
            {
                DiagnosticsBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(group, index);
                Grid.SetColumn(group, 0);
            }

            index++;
        }

        // The panel itself: a bar spans the width it was given, a rail keeps the width it was designed for.
        DiagnosticsPanel.MaxWidth = sheet ? double.PositiveInfinity : 400;
        DiagnosticsPanel.HorizontalAlignment = sheet ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        DiagnosticsPanel.VerticalAlignment = sheet ? VerticalAlignment.Bottom : VerticalAlignment.Top;
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Claim the pad for the console, or release it.
    ///
    /// <para>
    /// Tied to the session actually being live rather than to this page existing, which preserves the previous
    /// behaviour and is the behaviour you want: while "Connecting…" is on screen the pad should still drive the
    /// interface, so someone can back out of a console that is not answering.
    /// </para>
    /// </summary>
    private void UpdateSessionScope(bool live)
    {
        if (live && _sessionScope is null)
        {
            _sessionScope = new ShellInputScope(
                InputScopeKind.Session,

                // Forwarding resumes when this scope is on top and stops when anything covers it. Setting
                // SuspendInputForwarding also pushes one neutral frame to the console, which is what releases
                // whatever was physically held at that moment — the combination that opened a dialog must not
                // stay down inside the game underneath.
                onActivated: () => SetForwarding(true),
                onDeactivated: () => SetForwarding(false));

            // Deliberately no focusRoot: a session has nothing to come back to. Uncovering a stream should
            // resume forwarding and put keys back on the video surface, not restore a caret onto whichever
            // HUD button happened to be focused when a dialog opened over it.

            App.Input.Scopes.Push(_sessionScope);
            return;
        }

        if (!live)
        {
            ReleaseSessionScope();
        }
    }

    private void ReleaseSessionScope()
    {
        if (_sessionScope is { } scope)
        {
            App.Input.Scopes.Pop(scope);
            _sessionScope = null;
        }
    }

    private void SetForwarding(bool on)
    {
        if (_controller is not null)
        {
            _controller.SuspendInputForwarding = !on;
        }
    }

    private void OnDeviceLost(int reason)
    {
        _dispatcherQueue.TryEnqueue(() => ShowStatus(
            "Graphics device was reset",
            "The display driver restarted (this can happen after a driver update or if an external GPU was "
            + $"unplugged). Go back and reconnect to resume. [0x{reason:X8}]",
            terminal: true));
    }

    // ---- immersive mode ----

    /// <summary>The shell, which owns presenter and chrome decisions a Page cannot make.</summary>
    private IShellNavigator Shell => _services.Shell;

    /// <summary>
    /// Fullscreen with the chrome out of the way, and the display kept awake. A remote play stream is the whole
    /// point of the window while it is running; it previously rendered inside a nav pane and title bar, and
    /// nothing stopped the screen blanking mid-game because a gamepad is not "user activity" to Windows.
    /// </summary>
    private void EnterImmersiveMode()
    {
        KeepDisplayAwake(true);

        if (_settings.FullScreenOnConnect && !_enteredFullScreen)
        {
            Shell.SetFullScreen(true);
            _enteredFullScreen = true;
        }
    }

    private void LeaveImmersiveMode()
    {
        KeepDisplayAwake(false);

        if (_enteredFullScreen)
        {
            Shell.SetFullScreen(false);
            _enteredFullScreen = false;
        }
    }

    /// <summary>Toggle fullscreen without disturbing the session. Bound to F11.</summary>
    private void ToggleFullScreen()
    {
        bool goingFullScreen = !Shell.IsFullScreen;
        Shell.SetFullScreen(goingFullScreen);
        _enteredFullScreen = goingFullScreen;
    }

    private static void KeepDisplayAwake(bool keepAwake)
    {
        // ES_CONTINUOUS resets the idle timers; dropping the flags restores normal power behaviour. P/Invoked
        // rather than using DisplayRequest, which is unreliable in a WinUI 3 desktop app.
        const uint EsContinuous = 0x80000000;
        const uint EsDisplayRequired = 0x00000002;
        const uint EsSystemRequired = 0x00000001;

        _ = SetThreadExecutionState(keepAwake
            ? EsContinuous | EsDisplayRequired | EsSystemRequired
            : EsContinuous);
    }

    // DllImport rather than the source-generated LibraryImport: the latter emits unsafe marshalling code and so
    // requires AllowUnsafeBlocks for the entire project, which is a poor trade for a single call taking and
    // returning a uint. There is nothing to marshal here.
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private Task ShowExitHintBriefly()
    {
        if (_settings.ExitGesture == ExitGesture.None)
        {
            ExitHintText.Text = "Press Esc to leave the stream";
        }
        else
        {
            ExitHintText.Text =
                $"{ExitGestureDetector.Describe(_settings.ExitGesture)} to leave · Esc for windowed";
        }

        ExitHint.Visibility = Visibility.Visible;
        return HideExitHintAfterDelay();
    }

    /// <summary>
    /// Fade the hint out after a few seconds. Guarded because the page can be torn down while this is pending,
    /// and touching XAML after unload would throw on a thread nobody is watching.
    /// </summary>
    private async Task HideExitHintAfterDelay()
    {
        long token = ++_hintToken;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
        }
        catch (Exception)
        {
            return;
        }

        // A newer hint superseded this one, or the page is going away.
        if (token != _hintToken || _leaving)
        {
            return;
        }

        ExitHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>Distinguishes successive hints so an older timer cannot hide a newer message.</summary>
    private long _hintToken;

    // ---- leaving ----

    /// <summary>
    /// Escape steps back one level rather than ending everything at once: fullscreen → windowed → leave the
    /// session. Dropping straight out of a live session on a single Escape is a lot of destruction for one
    /// keypress, and "let me see the desktop for a moment without disconnecting" is the more common intent.
    /// </summary>
    /// <summary>
    /// The session's own keys: Escape steps out, F11 toggles fullscreen, F3 toggles diagnostics. True when one
    /// was handled.
    ///
    /// <para>
    /// Declines while a popup or dialog is open, because Escape belongs to that first — the disconnect prompt
    /// is a dialog, and intercepting Escape ahead of it would make the prompt undismissable. This runs on a
    /// tunnelling handler at the window root, so it sees the key before the dialog does and has to say so.
    /// </para>
    /// </summary>
    private bool TryHandleReservedKey(Windows.System.VirtualKey key)
    {
        if (XamlRoot is not null && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0)
        {
            return false;
        }

        switch (key)
        {
            case Windows.System.VirtualKey.Escape:
                if (Shell.IsFullScreen)
                {
                    LeaveImmersiveMode();
                    ShowWindowedHintBriefly();
                }
                else
                {
                    LeaveSession();
                }

                return true;

            case Windows.System.VirtualKey.F11:
                ToggleFullScreen();
                return true;

            case Windows.System.VirtualKey.F3:
                ToggleDiagnosticsPanel();
                return true;

            default:
                return false;
        }
    }

    private void ExitAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (Shell.IsFullScreen)
        {
            LeaveImmersiveMode();
            ShowWindowedHintBriefly();
            return;
        }

        LeaveSession();
    }

    private void FullScreenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullScreen();
    }

    /// <summary>Tell the user Escape again will disconnect, so the two-step behaviour is discoverable.</summary>
    private void ShowWindowedHintBriefly()
    {
        ExitHintText.Text = "Windowed — press F11 for full screen, or Esc again to disconnect";
        ExitHint.Visibility = Visibility.Visible;
        _ = HideExitHintAfterDelay();
    }

    private void LeaveButton_Click(object sender, RoutedEventArgs e) => LeaveSession();

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        // A fresh controller: the previous one reached a terminal state and will not restart.
        _ = RestartSessionAsync();
    }

    private async Task RestartSessionAsync()
    {
        ShowStatus("Connecting…", "Starting a new session.", terminal: false);
        await TeardownControllerAsync();
        await StartSessionAsync();
    }

    /// <summary>
    /// Dismiss the stream layer. That unloads this page, which triggers Page_Unloaded and the async teardown.
    ///
    /// <para>
    /// For a live session this first confirms (unless the user turned that off), letting them pick rest-vs-awake
    /// for this disconnect. A non-live session — still connecting, or already failed — skips the prompt: there is
    /// nothing to rest, and a second dialog on the way out of a failed connect is just friction.
    /// </para>
    /// </summary>
    private async void LeaveSession()
    {
        if (_leaving || _confirmingLeave)
        {
            return;
        }

        bool isLive = _controller?.CurrentStatus.IsLive == true;
        bool restMode = isLive && _settings.RestConsoleOnDisconnect;

        if (isLive && _settings.ConfirmOnDisconnect)
        {
            _confirmingLeave = true;

            // The prompt owns the pad while it is up. That is ModalHost's doing now rather than this
            // method's, and it is what stops presses leaking into the game behind it: the session's
            // deactivation edge suspends forwarding and releases whatever is held, and the matching pop
            // restores forwarding on every exit path — including "Stay connected", where the session keeps
            // running. This site is where that was first got right; it is now what every dialog gets.
            try
            {
                var dialog = new DisconnectDialog(_settings.RestConsoleOnDisconnect) { XamlRoot = XamlRoot };
                if (await ModalHost.ShowAsync(dialog) != ContentDialogResult.Primary)
                {
                    return; // "Stay connected" — the session keeps running
                }

                restMode = dialog.RestConsole;
            }
            catch (Exception)
            {
                // A dialog that cannot show (e.g. torn-down XamlRoot) must not trap the user in the session.
                // Fall through and leave using the standing default.
            }
            finally
            {
                _confirmingLeave = false;
            }
        }

        if (_leaving)
        {
            return; // teardown began while the dialog was up
        }

        _leaving = true;
        _connectCts?.Cancel();

        // The rest choice is made here, at disconnect, not frozen at connect — so push it to the live session
        // before teardown reads it.
        if (_controller is not null)
        {
            _controller.RestConsoleOnDisconnect = restMode;
        }

        LeaveImmersiveMode();
        // Hand the console and the rest-on-disconnect intent to the window so the consoles list can re-probe
        // and, if we asked this console to rest, watch it settle.
        Shell.CloseStream(_console?.Host, restMode);
    }

    // ---- input ----

    /// <summary>
    /// A pad was attached or detached. The transport enum is mapped onto the portable one here because it lives
    /// in the Windows-only input assembly; everything after that — the set, and what the label reads for none,
    /// one or several — belongs to the view-model.
    /// </summary>
    private void OnConnectionChanged(ControllerConnectionEvent evt)
        => _viewModel.ApplyControllerConnection(
            evt.ControllerId,
            evt.Connected,
            evt.Transport switch
            {
                ControllerTransport.Usb => ControllerLink.Usb,
                ControllerTransport.Bluetooth => ControllerLink.Bluetooth,
                _ => ControllerLink.Unknown,
            },
            evt.Source);

    private void OnPadFrame(ControllerStateFrame frame)
    {
        // Runs on the input thread. The exit gesture is evaluated here rather than in MainWindow because
        // MainWindow deliberately ignores the pad while a stream is capturing it.
        bool exit = _exitDetector?.Update(frame, DateTimeOffset.UtcNow) == true;
        double progress = _exitDetector?.Progress ?? 0;

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (exit)
            {
                LeaveSession();
                return;
            }

            ExitProgressBar.Value = progress;
            ExitProgressPanel.Visibility = progress is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;

            if (_viewModel.State.Rung == DiagnosticsRung.Full)
            {
                ControllerButtonsText.Text = $"Buttons: {frame.Buttons}";
                ControllerSticksText.Text =
                    $"L: ({frame.LeftStickX:F2}, {frame.LeftStickY:F2})  R: ({frame.RightStickX:F2}, {frame.RightStickY:F2})";
                ControllerTriggersText.Text = $"LT: {frame.LeftTrigger:F2}  RT: {frame.RightTrigger:F2}";
            }
        });
    }

    // ---- panel geometry ----

    private void OnVideoPanelSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSwapChainSize();

    private void OnVideoPanelScaleChanged(SwapChainPanel sender, object args) => UpdateSwapChainSize();

    private void UpdateSwapChainSize()
    {
        if (_pipeline is null)
        {
            return;
        }

        float scaleX = VideoPanel.CompositionScaleX;
        float scaleY = VideoPanel.CompositionScaleY;
        int width = Math.Max(1, (int)Math.Round(VideoPanel.ActualWidth * scaleX));
        int height = Math.Max(1, (int)Math.Round(VideoPanel.ActualHeight * scaleY));

        _pipeline.Resize(width, height);
        _pipeline.SetCompositionScale(scaleX, scaleY);
    }

    // ---- diagnostics ----

    /// <summary>
    /// Move focus off the on-screen chrome after it is used.
    ///
    /// <para>
    /// No longer required for key delivery — that is handled at the window root, independent of focus — but it
    /// still stops a button keeping a visible focus ring and being re-triggered by Enter. Only when keyboard input
    /// is enabled: stealing focus otherwise would break Tab access to those buttons for someone who never wanted
    /// keys sent to the console.
    /// </para>
    /// </summary>
    private void FocusStreamSurface()
    {
        if (_settings.InputBindings.KeyboardEnabled)
        {
            // The one place Programmatic is right, and the exception to the rule everywhere else that focus
            // must be visible: this takes focus so keystrokes reach the console, not so the user can see where
            // they are. A focus visual drawn around the video would be a rectangle over the game.
            Focus(FocusState.Programmatic);
        }
    }

    private void StreamSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        FocusStreamSurface();
        ShowTouchControls();
    }

    private void StreamSurface_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowTouchControls();

    /// <summary>
    /// Reveal the on-screen controls and restart their idle timer.
    ///
    /// <para>
    /// Summoned rather than permanent: the bar carries the only route to the PS button on hardware that has none,
    /// so it has to be reachable — but it sits over the game, so it must not stay. Any pointer or touch activity
    /// brings it back.
    /// </para>
    /// </summary>
    private void ShowTouchControls()
    {
        TouchControls.Visibility = Visibility.Visible;

        _touchControlsTimer ??= new DispatcherTimer { Interval = TouchControlsIdleTimeout };
        _touchControlsTimer.Tick -= TouchControlsTimer_Tick;
        _touchControlsTimer.Tick += TouchControlsTimer_Tick;
        _touchControlsTimer.Stop();
        _touchControlsTimer.Start();
    }

    private void TouchControlsTimer_Tick(object? sender, object e)
    {
        _touchControlsTimer?.Stop();

        // Never hide mid-press: releasing a button the user is still holding would send a phantom release, and
        // hiding the control they are touching is its own small betrayal.
        if (_virtualButtonsHeld != ControllerButtons.None)
        {
            ShowTouchControls();
            return;
        }

        TouchControls.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Press a console button from the bar. Pointer events rather than Click, because a hold must read as a hold —
    /// holding PS opens the console's power menu, and a Click handler can only ever express a tap.
    /// </summary>
    /// <summary>
    /// Wire the console buttons' pointer events.
    ///
    /// <para>
    /// Registered in code with <c>handledEventsToo: true</c>, which is the whole reason this method exists: WinUI's
    /// ButtonBase handles PointerPressed and PointerReleased itself to drive its visual states, and marks them
    /// handled before any XAML-declared handler runs. Wiring them as XAML attributes therefore looked correct and
    /// silently never fired — the buttons appeared and did nothing. XAML attribute syntax has no way to opt into
    /// handled events, so this cannot be expressed in markup.
    /// </para>
    ///
    /// <para>
    /// Click would have worked, but only as a tap: holding PS opens the console's power menu, and press/release must
    /// stay distinct for that.
    /// </para>
    /// </summary>
    private void WireConsoleButtons()
    {
        foreach (Button button in new[] { PsButton, CreateButton, OptionsButton, TouchpadButton })
        {
            button.AddHandler(
                PointerPressedEvent, new PointerEventHandler(ConsoleButton_PointerPressed), handledEventsToo: true);
            button.AddHandler(
                PointerReleasedEvent, new PointerEventHandler(ConsoleButton_PointerReleased), handledEventsToo: true);

            // A drag off the button must release it, or it stays asserted for the rest of the session.
            button.AddHandler(
                PointerCaptureLostEvent, new PointerEventHandler(ConsoleButton_PointerReleased), handledEventsToo: true);

            // Nothing can be sent without an input source, so say so rather than offering a dead control.
            button.IsEnabled = _inputSource is not null;
        }
    }

    private void ConsoleButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name } || !Enum.TryParse(name, out ControllerButtons button))
        {
            return;
        }

        _virtualButtonsHeld |= button;
        _inputSource?.SetVirtualButton(button, pressed: true);
        ShowTouchControls();
    }

    private void ConsoleButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name } || !Enum.TryParse(name, out ControllerButtons button))
        {
            return;
        }

        // PointerCaptureLost is wired to this too: a drag off the button must release it, or it stays held forever.
        _virtualButtonsHeld &= ~button;
        _inputSource?.SetVirtualButton(button, pressed: false);
        ShowTouchControls();
    }



    /// <summary>
    /// Bound the diagnostics panel to the window so its body scrolls instead of overflowing.
    ///
    /// <para>
    /// Applied from code because the panel is top-aligned: without a ceiling it simply grows past the bottom of the
    /// window and the last rows are unreachable, which is what happened at 7 inches (the power row was cut in half).
    /// Stretching it instead would make it full height even when nearly empty.
    /// </para>
    /// </summary>
    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyDiagnosticsHeightLimit();

    private void ApplyDiagnosticsHeightLimit()
    {
        // The panel's own 16px margins top and bottom, plus a little room so it never touches the edge.
        double available = ActualHeight - 48;
        DiagnosticsPanel.MaxHeight = available > 120 ? available : 120;

        ApplyDiagnosticsPlacement();
    }

    /// <summary>
    /// Put the instrument panel where the video isn't.
    ///
    /// <para>
    /// The stream is 16:9 and the window usually is not, so there is nearly always a bar that is already black
    /// and carrying nothing. Claiming it costs the player no picture — which is also the answer to "nobody
    /// leaves the panel open": today it is expensive to, and it does not have to be.
    /// </para>
    ///
    /// <para>
    /// Only the pillarbox case is wired here. The panel is already a tall narrow column, so moving it into a
    /// pillar is a position change; the letterbox <see cref="DiagnosticsPlacement.Sheet"/> wants a four-column
    /// re-flow that does not exist yet, and until it does that case is handled as an overlay — which is what
    /// happens today, so nothing regresses while it is missing.
    /// </para>
    /// </summary>
    private void ApplyDiagnosticsPlacement()
    {
        SessionDiagnosticsState d = _viewModel.State.Diagnostics;
        HudLayout layout = HudPlacement.For(ActualWidth, ActualHeight, d.VideoWidth, d.VideoHeight);

        // The panel is MaxWidth-constrained rather than fixed, so Width is NaN until it has been measured -
        // and NaN propagates silently through Math.Max into a Thickness nobody can read.
        double panelWidth = DiagnosticsPanel.ActualWidth > 0
            ? DiagnosticsPanel.ActualWidth
            : DiagnosticsPanel.MaxWidth;

        ApplyDiagnosticsLayout(layout.Placement);

        // A sheet must not grow past the bar it is sitting in. Without this it keeps the window-height cap set
        // above and quietly covers the picture - which is the one thing claiming dead space exists to avoid.
        if (layout.Placement == DiagnosticsPlacement.Sheet)
        {
            DiagnosticsPanel.MaxHeight = Math.Max(120, layout.BarHeight - 32);
        }

        // Centre the panel in the pillar it is claiming rather than pinning it to the window edge: a rail
        // hard against the bezel reads as something that fell off the side.
        double inset = layout.Placement == DiagnosticsPlacement.Rail && double.IsFinite(panelWidth)
            ? Math.Max(16, (layout.PillarWidth - panelWidth) / 2)
            : 16;

        // Only when it actually moves. This runs on every state change, and reassigning a Thickness
        // invalidates layout whether or not the value differs.
        if (Math.Abs(inset - _appliedDiagnosticsInset) > 0.5)
        {
            _appliedDiagnosticsInset = inset;
            DiagnosticsPanel.Margin = new Thickness(inset, 16, 16, 16);
        }
    }

    private void SaveDiagnosticsAccelerator_Invoked(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SaveDiagnostics();
    }

    private void SaveDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        SaveDiagnostics();
        FocusStreamSurface();
    }

    /// <summary>
    /// Write a plain-text snapshot of everything the overlay knows to the state directory.
    ///
    /// <para>
    /// This exists for machines with no development environment — a handheld, in practice — where the overlay can
    /// be photographed but nothing can be attached to the process, ETW is impractical to collect, and
    /// Debug.WriteLine goes nowhere. The report is deliberately plain text and self-contained so it can be read
    /// anywhere and pasted whole.
    /// </para>
    /// </summary>
    /// <summary>
    /// Open the session trace. One CSV per session, beside the F8 reports, recording every stats tick.
    ///
    /// <para>
    /// The F8 report answers "what is wrong right now"; this answers "what happened over the last ten
    /// minutes", which is the shape of question a threshold needs. Reading a twice-a-second figure off a
    /// screen and trying to catch its peak is not a measurement, and it is what the receive-queue question
    /// currently asks of whoever is holding the handheld.
    /// </para>
    /// </summary>
    private void StartTrace()
    {
        try
        {
            string path = System.IO.Path.Combine(
                _services.Paths.StateDirectory,
                $"session-trace-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            _trace = new StreamWriter(path, append: false) { AutoFlush = false };
            _tracePreambleWritten = false;
            _tracePreambleWaits = 0;

            // Say WHERE, for the same reason the F8 report does: on a handheld there is no other way to
            // find it, and a trace nobody can locate is a trace nobody sends.
            _tracePath = path;
            DiagnosticsSavedText.Text = $"tracing to: {path}";

            // The preamble is deliberately NOT written here - see WriteTracePreamble.
        }
        catch (Exception ex)
        {
            // Same rule as the F8 report: a diagnostics action never takes a live session with it.
            _trace = null;
            Debug.WriteLine($"[Ripcord] session trace could not be started: {ex}");
        }
    }

    /// <summary>Rows to wait for an adapter name before writing the preamble without one.</summary>
    private const int TracePreambleMaxWaits = 40;

    /// <summary>
    /// Write the preamble, once, as late as the adapter name allows.
    ///
    /// <para>
    /// The preamble exists so a trace that arrives on its own still says what it was a trace OF, and the GPU
    /// is the one piece of machine detail in it - "which adapter" changes the answer to most questions a
    /// trace is taken to settle. But the adapter is only resolved when the decode pipeline initialises, and
    /// that happens on the first decoded frame. <see cref="StartTrace"/> runs at the top of the connect,
    /// before there is a pipeline at all, so asking then reliably yields an empty string - which is exactly
    /// what the first traces taken off hardware contained.
    /// </para>
    ///
    /// <para>
    /// So the preamble waits for a name, but not indefinitely: after <see cref="TracePreambleMaxWaits"/> rows
    /// it is written regardless. A session that fails before it ever decodes is precisely when a trace is most
    /// worth having, and a file of bare numbers with no header is not one.
    /// </para>
    /// </summary>
    private void WriteTracePreamble()
    {
        if (_trace is null || _tracePreambleWritten)
        {
            return;
        }

        string adapter = _pipelineStats.AdapterDescription;
        if (string.IsNullOrWhiteSpace(adapter))
        {
            if (++_tracePreambleWaits < TracePreambleMaxWaits)
            {
                return;
            }

            // Said plainly rather than left blank: "not known yet" and "there is no GPU row at all" are
            // different findings, and a reader months later cannot tell an empty field from a missing one.
            adapter = "unknown (no frame decoded)";
        }

        SessionConfig config = _settings.ToSessionConfig();
        foreach (string line in SessionSampleLog.Preamble(
            typeof(SessionPage).Assembly.GetName().Version?.ToString() ?? "unknown",
            adapter, config.CodecPreference.ToString(),
            _settings.Width, _settings.Height, _settings.TargetFps, _settings.BitrateKbps))
        {
            _trace.WriteLine(line);
        }

        _tracePreambleWritten = true;
        _trace.Flush();
    }

    /// <summary>
    /// One row. Flushed on a cadence rather than every line: a trace that loses its last second to a hard
    /// kill is still useful, and a trace that costs a disk write twice a second on a handheld is not.
    /// </summary>
    private void AppendTrace()
    {
        if (_trace is null || _viewModel.LastSample is not { } sample)
        {
            return;
        }

        try
        {
            // Rows written before the header would be unreadable, so the preamble gates them. The wait is
            // bounded (see WriteTracePreamble) and what it costs is the pre-connect all-zero rows.
            WriteTracePreamble();
            if (!_tracePreambleWritten)
            {
                return;
            }

            _trace.WriteLine(SessionSampleLog.Row(sample));

            if (++_traceRowsSinceFlush >= 20)
            {
                _traceRowsSinceFlush = 0;
                _trace.Flush();
            }
        }
        catch (Exception ex)
        {
            // Stop tracing rather than throwing every tick for the rest of the session.
            Debug.WriteLine($"[Ripcord] session trace stopped: {ex}");
            StopTrace();
        }
    }

    private void StopTrace()
    {
        StreamWriter? trace = _trace;
        _trace = null;

        try
        {
            trace?.Flush();
            trace?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Ripcord] session trace close failed: {ex}");
        }
    }

    private void SaveDiagnostics()
    {
        try
        {
            string directory = _services.Paths.StateDirectory;

            // Timestamped rather than overwritten: comparing two runs is the usual reason to capture one, and a
            // single rolling file makes that impossible.
            string path = System.IO.Path.Combine(
                directory,
                $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            File.WriteAllText(path, BuildDiagnosticsReport());

            // Show WHERE it went. On a handheld there is no other way to find out.
            DiagnosticsSavedText.Text = _tracePath is null
                ? $"saved: {path}"
                : $"saved: {path}  ·  trace: {_tracePath}";
        }
        catch (Exception ex)
        {
            // Never let a diagnostics action be the thing that kills a live session.
            DiagnosticsSavedText.Text = $"could not save: {ex.Message}";
            Debug.WriteLine($"[Ripcord] diagnostics save failed: {ex}");
        }
    }

    /// <summary>
    /// The saved diagnostics text. The report itself is composed in <see cref="SessionDiagnosticsReport"/>; what
    /// this supplies is the handful of facts only a Windows front end can answer.
    /// </summary>
    private string BuildDiagnosticsReport() => SessionDiagnosticsReport.Build(
        DateTimeOffset.Now,
        new DiagnosticsHostInfo(
            AppVersion: typeof(SessionPage).Assembly.GetName().Version?.ToString() ?? "unknown",
            OperatingSystem: $"{Environment.OSVersion} ({RuntimeInformation.OSArchitecture})",
            InputSourceName: App.Input.SourceName,
            Lifecycle: _controller?.CurrentStatus.Lifecycle,
            LifecycleDetail: _controller?.CurrentStatus.Detail ?? string.Empty,
            ReconnectAttempt: _controller?.CurrentStatus.ReconnectAttempt ?? 0),
        _settings,
        _viewModel.State,
        ReadTelemetry(),
        _pipelineStats,
        _viewModel.FpsHistory,
        _viewModel.LossHistory,
        _viewModel.RttHistory,
        _viewModel.BitrateHistory);


    /// <summary>
    /// Route a key press to the console. Marked handled only when the key is actually bound, so unbound keys still
    /// reach the window's own shortcuts.
    /// </summary>
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The keys the session reserves, handled HERE rather than as accelerators on the page.
        //
        // A KeyboardAccelerator only fires while focus is inside its scope, and focus leaves this page as soon
        // as the video is clicked -- SwapChainPanel is not focusable, and FocusStreamSurface only takes focus
        // when keyboard input is enabled, which is off by default. So Escape, F11 and F3 all worked until the
        // first click on the stream and were dead afterwards: reported as "Esc does nothing, F11 still
        // toggles", which was simply whether focus had moved yet. Game input was already routed here for
        // exactly this reason; the reserved keys were left behind.
        if (TryHandleReservedKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (_inputSource is null || TypingSomewhere())
        {
            return;
        }

        // e.Handled suppresses further routing, which is what stops a bound key from ALSO triggering a menu
        // mnemonic or the XAML focus engine while the stream has it.
        e.Handled = _inputSource.KeyDown((int)e.Key);
    }


    /// <summary>
    /// Whether a text-entry control currently has focus.
    ///
    /// <para>
    /// The key handlers sit on the window root so that delivery does not depend on focus, but that breadth cuts
    /// both ways: without this check, a bound key such as W would be swallowed before a text box could see it, and
    /// anything typed elsewhere in the window while a session page exists would silently go to the console instead
    /// of into the field.
    /// </para>
    /// </summary>
    private bool TypingSomewhere()
        => FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox;

    private void OnPageKeyUp(object sender, KeyRoutedEventArgs e)
    {
        // No typing guard on release: if a key went to the console on press, its release must reach the console
        // too, or focus moving to a text box mid-press would leave that key held forever.
        if (_inputSource is null)
        {
            return;
        }

        e.Handled = _inputSource.KeyUp((int)e.Key);
    }

    /// <summary>Release every held key when the window is deactivated (see the subscription for why).</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            _inputSource?.ReleaseAllKeys();

            // Same reasoning as the keys: a virtual button held when focus left would stay asserted indefinitely.
            _virtualButtonsHeld = ControllerButtons.None;
            _inputSource?.ReleaseVirtualButtons();
        }
    }

    private void ToggleDiagnostics(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleDiagnosticsPanel();
        args.Handled = true;
    }

    private void DiagnosticsDetailButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ShowDiagnosticsDetail();

        // Same reason as the touch toggle: the button would otherwise keep focus and swallow every
        // subsequent Space before it reached the console.
        FocusStreamSurface();
    }

    private void DiagnosticsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleDiagnosticsPanel();

        // Hand focus back, or the button keeps it and every subsequent Space re-toggles the panel instead of
        // reaching the console.
        FocusStreamSurface();
    }

    /// <summary>
    /// Show the HUD or hide it, at whatever rung it was last at. Never a step deeper — see
    /// <see cref="SessionViewModel.ToggleDiagnostics"/> for why that matters.
    /// </summary>
    private void ToggleDiagnosticsPanel()
    {
        _viewModel.ToggleDiagnostics();

        // Catch it up in one pass: rendering is skipped while a rung is hidden, so without this it would show
        // the last sample from before it was closed until the next tick. Render already ran for the text;
        // the graph is the part it does not cover.
        if (_viewModel.State.Rung == DiagnosticsRung.Full)
        {
            RenderGraph();
        }
    }

    /// <summary>
    /// One diagnostics sample.
    ///
    /// <para>
    /// Deliberately does NOT require a live session. The panel is least useful when it is empty, which is
    /// exactly while something is failing to connect — and the GPU, decoder and decode-path rows are already
    /// meaningful before any session exists.
    /// </para>
    ///
    /// <para>
    /// Everything this used to compute — rates, thresholds, wording, which rows have anything to say — moved to
    /// <see cref="SessionViewModel.Sample"/>. What is left is reading the two live objects and redrawing the
    /// sparklines, which are the one part of the readout that needs a canvas.
    /// </para>
    /// </summary>
    private void StatsTick(object? sender, object e)
    {
        // SAMPLED whether or not the panel is showing; only the DRAWING is gated on visibility, further down in
        // Render. Gating the sample itself cost two things, and a saved report showed both: the health verdict
        // could read "Not connected yet" beside a lifecycle of "Streaming", because it had been composed before
        // the session existed and nothing recomposed it; and the sparklines opened empty, so someone who
        // reached for the overlay because something looked wrong got no history of the thirty seconds that made
        // them reach for it.
        //
        // False means the sample was skipped — no pipeline yet, or too little time since the last one for a rate
        // to mean anything — so there is nothing new to plot either.
        // The connect gate needs the clock, not just reports. ConnectFlow reports only when something
        // changes, so a stage that hangs reports once and then goes quiet - without this, a four-second
        // stall would never promote its own line and the screen would sit on a stage that had already
        // stopped being true. Ticked before sampling, and unconditionally, because the connect sequence runs
        // before there is any telemetry to sample.
        _viewModel.TickConnect();

        bool sampled = _viewModel.Sample(ReadTelemetry());

        if (sampled)
        {
            AppendTrace();
        }

        if (sampled && _viewModel.State.Rung == DiagnosticsRung.Full)
        {
            RenderGraph();
        }
    }

    /// <summary>Everything the view-model needs from the controller and the power monitor, as plain values.</summary>
    private SessionTelemetry ReadTelemetry()
    {
        if (_controller is not { } controller)
        {
            return SessionTelemetry.None with { Power = _powerMonitor?.Current };
        }

        return new SessionTelemetry(
            HasSession: true,
            Statistics: controller.LastStatistics ?? new SessionStatistics(0, 0, 0, 0, 0),
            RecommendedQuality: controller.RecommendedQuality,
            QualityReason: controller.QualityReason,
            MillisecondsSinceConnect: controller.MillisecondsSinceConnect,
            MillisecondsSinceLastFrame: controller.MillisecondsSinceLastFrame,
            Power: _powerMonitor?.Current);
    }

    /// <summary>
    /// Plot the four series into the canvas. Each has its own full scale (they share no units), so the graph is
    /// about shape over time rather than comparing absolute heights between lines — which is exactly the
    /// question "is this steady or is it oscillating?".
    /// </summary>
    private void RenderGraph()
    {
        MetricHistory fps = _viewModel.FpsHistory;
        MetricHistory rtt = _viewModel.RttHistory;
        MetricHistory loss = _viewModel.LossHistory;
        MetricHistory bitrate = _viewModel.BitrateHistory;

        if (fps.Count < 2)
        {
            return;
        }

        // Frame rate is scaled against the requested rate with headroom, so "at target" sits high but not
        // clipped and a shortfall is immediately visible as a drop. The other three scales belong to the
        // view-model, because each is derived from a threshold it already reasons about.
        SessionDiagnosticsState d = _viewModel.State.Diagnostics;

        PlotSpark(FpsLine, FpsSpark, fps, _viewModel.FramesPlot, d.FramesSeverity);
        PlotSpark(RttLine, RttSpark, rtt, SessionViewModel.LatencyPlot, d.LatencySeverity);
        PlotSpark(LossLine, LossSpark, loss, SessionViewModel.LossPlot, d.LossSeverity);

        // Bitrate never carries a severity: there is no bitrate that is wrong by itself.
        PlotSpark(BitrateLine, BitrateSpark, bitrate, _viewModel.BitratePlot, MetricSeverity.Normal);

        // Value and peak beside each line, because a sparkline shows shape and says nothing about magnitude.
        FpsValueText.Text = $"{fps.Latest:F0}";
        FpsPeakText.Text = $"{fps.Max():F0}";
        RttValueText.Text = $"{rtt.Latest:F1} ms";
        RttPeakText.Text = $"{rtt.Max():F1}";
        LossValueText.Text = $"{loss.Latest:F1}%";
        LossPeakText.Text = $"{loss.Max():F1}%";
        BitrateValueText.Text = $"{bitrate.Latest:F1}";
        BitratePeakText.Text = $"{bitrate.Max():F1}";
    }

    /// <summary>
    /// Draw one series into its own small canvas.
    ///
    /// <para>
    /// One canvas per metric rather than four series sharing one box. They have no common unit, so overlaying them
    /// invited exactly the wrong comparison — a tall loss line looked worse than a tall frame-rate line — and no
    /// line was identifiable. Each is now labelled, scaled independently, and sits beside its own value and peak.
    /// </para>
    /// </summary>
    private static void PlotSpark(
        Polyline line, Canvas host, MetricHistory history, MetricPlot plot, MetricSeverity severity)
    {
        // The stroke's colour is the reading, not the row's identity. These were fixed per metric in markup,
        // so the loss line was red at zero loss and the frames line green while frames collapsed - colour that
        // looked like it meant something and never did.
        if (SeverityBrush(severity) is { } stroke)
        {
            line.Stroke = stroke;
        }

        double fullScale = plot.FullScale;

        // ActualWidth is 0 until the first layout pass, and these canvases are star-sized so there is no declared
        // Width to fall back on — skip rather than draw a degenerate line at x=0.
        double width = host.ActualWidth;
        double height = host.ActualHeight > 0 ? host.ActualHeight : host.Height;
        if (width <= 0 || double.IsNaN(height) || height <= 0)
        {
            return;
        }

        var points = new PointCollection();
        double step = width / (history.Capacity - 1);

        // Right-align the series so the newest sample is always at the right edge and the plot fills in
        // leftwards as history accumulates, instead of the line sliding across the canvas as it fills.
        int offset = history.Capacity - history.Count;

        for (int i = 0; i < history.Count; i++)
        {
            double x = (offset + i) * step;
            double y = height - (history.NormalisedAt(i, fullScale) * height);
            points.Add(new Windows.Foundation.Point(x, y));
        }

        line.Points = points;
    }

    // ---- teardown ----

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        VideoPanel.SizeChanged -= OnVideoPanelSizeChanged;
        VideoPanel.CompositionScaleChanged -= OnVideoPanelScaleChanged;

        _statsTimer?.Stop();
        _statsTimer = null;

        _connectionsSubscription?.Dispose();
        if (_keyRoot is not null)
        {
            _keyRoot.PreviewKeyDown -= OnPageKeyDown;
            _keyRoot.PreviewKeyUp -= OnPageKeyUp;
            _keyRoot = null;
        }
        if (App.MainWindow is { } window)
        {
            window.Activated -= OnWindowActivated;
        }

        App.Input.FrameReceived -= OnPadFrame;
        ReleaseSessionScope();

        _inputSource?.Dispose();
        _inputSource = null;

        LeaveImmersiveMode();

        // Fire-and-forget the async teardown. The previous version blocked the UI thread on
        // DisposeAsync().AsTask().Wait() twice, which froze the window on exit and risked a deadlock: session
        // disposal awaits the control keep-alive task and joins the decode worker.
        _ = TeardownAsync();
    }

    private async Task TeardownAsync()
    {
        StopTrace();

        await TeardownControllerAsync();

        D3D12VideoDecodePipeline? pipeline = _pipeline;
        _pipeline = null;

        // Detach first: a stats tick that lands mid-teardown must see "not ready" rather than a disposing device.
        _pipelineStats.Attach(null);

        if (pipeline is not null)
        {
            pipeline.DeviceLost -= OnDeviceLost;
            try { await pipeline.DisposeAsync(); } catch (Exception) { /* teardown races */ }
        }
    }

    private async Task TeardownControllerAsync()
    {
        _statusSubscription?.Dispose();
        _statusSubscription = null;

        SessionController? controller = _controller;
        _controller = null;
        if (controller is not null)
        {
            try { await controller.DisposeAsync(); } catch (Exception) { /* teardown races */ }
        }

        // The monitor owns a polling timer.
        _powerMonitor?.Dispose();
        _powerMonitor = null;
    }
}

/// <summary>
/// WinUI 3 SwapChainPanel native interop: associates a DXGI swap chain (created by the native D3D12 renderer)
/// with the XAML panel. Declared here because CsWinRT does not project this COM interface from
/// microsoft.ui.xaml.media.dxinterop.h.
/// </summary>
[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    [PreserveSig]
    int SetSwapChain(IntPtr swapChain);
}
