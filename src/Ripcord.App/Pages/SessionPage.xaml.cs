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
using Ripcord.Presentation.Sessions;
using Ripcord.Core.Security;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Discovery;
using Ripcord.Protocol.Halyard.Session;
using Ripcord_App.Dialogs;
using Ripcord_App.Services;
using WinRT;
using Ripcord.Core.Reactive;

namespace Ripcord_App.Pages;

/// <summary>
/// The streaming surface. Its job is now presentation only: <see cref="SessionController"/> owns the session
/// lifecycle (connect, degrade, reconnect, tear down), so this page renders status, routes controller input,
/// and handles the immersive-mode concerns a Page is actually responsible for.
/// </summary>
public sealed partial class SessionPage : Page
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly RipcordAppServices _services = App.Services;
    private readonly ISettingsStore _settingsStore = App.Services.Settings;

    private RipcordSettings _settings = new();
    private IControllerSource? _controllerSource;

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
    private IDisposable? _stateSubscription;

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

    private PairedConsole? _console;
    private IPowerThermalMonitor? _powerMonitor;
    private ExitGestureDetector? _exitDetector;
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

    /// <summary>
    /// True while controller input is being routed to the console. MainWindow's gamepad UI-navigation checks
    /// this so it does not consume the same pad — otherwise B would navigate back instead of reaching the
    /// console. The exit gesture (below) is what keeps that from making the stream inescapable.
    /// </summary>
    public bool IsCapturingInput => _controller?.CurrentStatus.IsLive == true;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _console = e.Parameter as PairedConsole;
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

        DiagnosticsPanel.Visibility = _settings.ShowDiagnosticsOverlay ? Visibility.Visible : Visibility.Collapsed;

        // Input set-up is ISOLATED and non-fatal. It reaches native code (GameInput via Ripcord.Input.Interop), and
        // a stream is perfectly watchable without a controller — so a failure here must degrade to "no input", never
        // prevent connecting. It previously ran unguarded ahead of the first status message, so any failure left the
        // static "Starting…" on screen with nothing reported anywhere.
        try
        {
            _controllerSource = ControllerSourceFactory.Create();
            InputEnginesText.Text = $"engines: {_controllerSource.SourceName}";

            _connectionsSubscription = _controllerSource.Connections.Subscribe(
                new AnonymousObserver<ControllerConnectionEvent>(OnConnectionChanged));
            _stateSubscription = _controllerSource.StateChanges(string.Empty).Subscribe(
                new AnonymousObserver<ControllerStateFrame>(OnStateChanged));

            // Keyboard support and the gamepad remap both live here. Built even when keyboard input is disabled, so
            // the remap still applies to pad frames.
            _inputSource = new MergedInputSource(
                _controllerSource.StateChanges(string.Empty), _settings.InputBindings);
        }
        catch (Exception ex)
        {
            _controllerSource = null;
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
        _connectCts = new CancellationTokenSource();
        if (_console is null)
        {
            ShowStatus("No console selected", "Choose a console from the Consoles page to start streaming.", terminal: true);
            return;
        }

        if (!IPAddress.TryParse(_console.Host, out IPAddress? address))
        {
            ShowStatus("Can't reach that console", $"'{_console.Host}' is not a valid IP address.", terminal: true);
            return;
        }

        SessionConfig config = _settings.ToSessionConfig();

        // PS4 Remote Play is H.264 / SDR only — HEVC and HDR are PS5 features. Requesting HEVC makes the
        // console reject the launchSpec silently (no SESSION_REPLY, so no stream), and the decoder codec must
        // match the launchSpec anyway. Force both for a PS4 regardless of the user's setting; the same config
        // feeds the launchSpec and the decode pipeline below, so they stay in agreement.
        if (string.Equals(_console?.Platform, "Ps4", StringComparison.OrdinalIgnoreCase))
        {
            config = config with { CodecPreference = VideoCodec.H264, RequestedDynamicRange = DynamicRange.Sdr };
        }

        // Each phase announces itself BEFORE it runs, so if one hangs the last message on screen names it. Video
        // device creation in particular is a plausible place to stall on unfamiliar hardware, and it used to be
        // indistinguishable from a network problem because the overlay said "Connecting…" throughout.
        ShowStatus("Preparing video…", "Creating the graphics device and decoder.", terminal: false);

        try
        {
            await InitVideoPipelineAsync(config);
        }
        catch (Exception ex)
        {
            ShowStatus("Video setup failed", ex.Message, terminal: true);
            return;
        }

        ShowStatus("Checking credentials…", "Loading control secrets and pairing.", terminal: false);
        // Diagnostics run from here on, before the session exists: an empty panel is least useful precisely while
        // something is failing to connect, and the GPU/adapter rows are already meaningful at this point.
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statsTimer.Tick += StatsTick;
        _statsTimer.Start();

        var secrets = HalyardControlSecretsLoader.Load(out string cryptoSource);
        var factory = new HalyardSessionFactory(secrets, new PairedConsoleCredentialStore(_services.Consoles));
        if (!factory.HasRealCrypto)
        {
            // FATAL, and it must say so. Without the control secrets the session crypto is a passthrough stub, so
            // the handshake can never complete — this used to be noted in the diagnostics panel and then the connect
            // was attempted anyway, leaving "Connecting…" on screen indefinitely with no stated cause. On a machine
            // with no debugger that is close to undiagnosable, so the message names the file AND the directory.
            // Normal builds bundle these constants, so reaching here means either this build omitted them
            // (-p:BundleInteropConstants=false) or an override path was set and is broken. Say both, because
            // on a machine with no debugger a wrong hint here is expensive.
            D3D12StatusText.Text = $"Control constants NOT loaded — {cryptoSource}";
            ShowStatus(
                "Missing control constants",
                "Streaming needs the protocol's control-plane constants, which are normally bundled with the "
                + "build. This build either omitted them (BundleInteropConstants=false) or has a broken "
                + $"override. To supply them explicitly, put control_crypto_vectors.json in "
                + $"{_services.Paths.ConfigDirectory} or point RIPCORD_CONTROL_FIXTURE at it, then "
                + $"reconnect.\n\nDetail: {cryptoSource}",
                terminal: true);
            return;
        }

        // Wake the console if it is in standby, before attempting to connect. A connect to a sleeping console
        // cannot succeed, and without this it just hung on "Connecting…" until timeout with no cause given.
        // Non-fatal except for the one case that genuinely blocks streaming (asked to wake, did not).
        if (!await EnsureConsoleAwakeAsync())
        {
            return;
        }

        // A real power monitor, so the adaptive controller's battery / energy-saver / critical-battery caps can
        // actually engage. Without one injected, SessionController falls back to UnknownPowerThermalMonitor,
        // which always claims external power — meaning a handheld on battery streamed at full desktop quality.
        _powerMonitor = PowerThermalMonitor.ForCurrentPlatform();

        ShowStatus("Connecting to your console…", "Control setup and stream negotiation.", terminal: false);

        // The controller owns everything from here: handshake, media/input routing, stall detection, reconnect.
        _controller = new SessionController(
            () => factory.Create(_console!.Id, address, loginPinProvider: RequestLoginPinAsync),
            _pipeline!,
            _inputSource,
            _powerMonitor);

        _statusSubscription = _controller.Status.Subscribe(
            new AnonymousObserver<SessionStatus>(OnStatusChanged));

        OnStatusChanged(_controller.CurrentStatus);
        await _controller.StartAsync(config);
    }

    /// <summary>
    /// Make sure the console is awake before connecting. Returns false only when we sent a wake and the
    /// console never came up — the one outcome that genuinely cannot lead to a stream, so the connect stops
    /// with a stated reason. Everything else (already awake, woke, or not answering discovery) proceeds:
    /// a console that does not answer SRCH may still be reachable, and letting the connect surface that is
    /// more useful than refusing to try.
    /// </summary>
    private async Task<bool> EnsureConsoleAwakeAsync()
    {
        var progress = new Progress<string>(line => ShowStatus(line, "The console was in standby.", terminal: false));

        ConsoleWakeOutcome outcome;
        try
        {
            // Everything family-specific about waking — ports, protocol versions, the pairing credential the
            // wake has to be signed with — now lives behind IConsoleWakeCoordinator.
            outcome = await _services.WakeCoordinator
                .EnsureAwakeAsync(_console!, progress, _connectCts!.Token);
        }
        catch (OperationCanceledException)
        {
            return false; // the user left the page mid-wake
        }

        if (outcome == ConsoleWakeOutcome.TimedOut)
        {
            ShowStatus(
                "Console didn't wake",
                "The console reported standby and did not wake within 30 seconds. Turn it on manually, or "
                + "check it is set to allow being woken from rest mode, then reconnect.",
                terminal: true);
            return false;
        }

        return true;
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
                ContentDialogResult result = await dialog.ShowAsync();
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
    /// Project the whole view-model state onto the controls. One method rather than per-property handlers,
    /// because the state arrives as one value and cannot be half-applied.
    /// </summary>
    private void Render(SessionViewState s)
    {
        StatusOverlay.Visibility = Vis(s.StatusVisible);
        StatusHeadline.Text = s.StatusHeadline;
        StatusDetail.Text = s.StatusDetail;
        StatusRing.IsActive = s.StatusBusy;
        StatusActions.Visibility = Vis(s.StatusActionsVisible);

        ControllerConnectedText.Text = s.ConnectedControllers;

        // The state is kept current twice a second regardless; assigning two dozen text properties on a
        // collapsed panel is the part worth skipping. ToggleDiagnosticsPanel renders on the way in, so opening
        // the overlay shows the latest sample rather than whatever was there when it was last closed.
        if (DiagnosticsPanel.Visibility == Visibility.Visible)
        {
            RenderDiagnostics(s.Diagnostics);
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
        RenderHealthDot(d.HealthLevel);
    }

    /// <summary>
    /// Rebuild the pill row, but only when the set has actually changed — this is the one part of the readout
    /// that creates elements rather than assigning text, and doing it twice a second churns layout for no
    /// visible difference. The view-model decides WHICH pills; this decides when redrawing is worth it.
    /// </summary>
    private void RenderCapabilityPills(SessionDiagnosticsState d)
    {
        if (d.CapabilityPillSignature == _renderedPillSignature)
        {
            return;
        }

        _renderedPillSignature = d.CapabilityPillSignature;
        CapabilityPills.Items.Clear();

        foreach (CapabilityPill pill in d.CapabilityPills)
        {
            CapabilityPills.Items.Add(new Border
            {
                Style = (Style)Resources[pill.Accent ? "AccentCapabilityPillStyle" : "CapabilityPillStyle"],
                Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock
                {
                    Text = pill.Label,
                    Style = (Style)Resources[pill.Accent ? "AccentCapabilityPillTextStyle" : "CapabilityPillTextStyle"],
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
    private void RenderHealthDot(StreamHealthLevel level)
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
            HealthDot.Fill = themed;
        }
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

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

            // The prompt owns the pad while it is up: suspend forwarding so button presses drive the dialog
            // (Disconnect / Stay connected) instead of leaking into the game behind it. Restored on every exit
            // path below — including "Stay connected", where the session keeps running.
            if (_controller is not null)
            {
                _controller.SuspendInputForwarding = true;
            }

            try
            {
                var dialog = new DisconnectDialog(_settings.RestConsoleOnDisconnect) { XamlRoot = XamlRoot };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
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

                // Resume forwarding whenever the session survives the prompt. When we go on to leave, teardown
                // stops the pad anyway, so resuming here is harmless in that case too.
                if (_controller is not null)
                {
                    _controller.SuspendInputForwarding = false;
                }
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

    private void OnStateChanged(ControllerStateFrame frame)
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

            if (DiagnosticsPanel.Visibility == Visibility.Visible)
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
            DiagnosticsSavedText.Text = $"saved: {path}";
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
            InputSourceName: _controllerSource?.SourceName ?? "none",
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

    private void DiagnosticsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleDiagnosticsPanel();

        // Hand focus back, or the button keeps it and every subsequent Space re-toggles the panel instead of
        // reaching the console.
        FocusStreamSurface();
    }

    private void ToggleDiagnosticsPanel()
    {
        bool showing = DiagnosticsPanel.Visibility != Visibility.Visible;
        DiagnosticsPanel.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;

        if (showing)
        {
            // Catch the panel up in one pass: rendering is skipped while it is collapsed, so without this it
            // would show the last sample from before it was closed until the next tick.
            RenderDiagnostics(_viewModel.State.Diagnostics);
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
        if (_viewModel.Sample(ReadTelemetry()) && DiagnosticsPanel.Visibility == Visibility.Visible)
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
        double fpsFullScale = Math.Max(1, _settings.TargetFps * 1.2);

        PlotSpark(FpsLine, FpsSpark, fps, fpsFullScale);
        PlotSpark(RttLine, RttSpark, rtt, SessionViewModel.RttFullScaleMs);
        PlotSpark(LossLine, LossSpark, loss, SessionViewModel.LossFullScalePercent);
        PlotSpark(BitrateLine, BitrateSpark, bitrate, _viewModel.BitrateFullScaleMbps);

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
    private static void PlotSpark(Polyline line, Canvas host, MetricHistory history, double fullScale)
    {
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
        _stateSubscription?.Dispose();
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

        _inputSource?.Dispose();
        _inputSource = null;
        (_controllerSource as IDisposable)?.Dispose();
        _controllerSource = null;

        LeaveImmersiveMode();

        // Fire-and-forget the async teardown. The previous version blocked the UI thread on
        // DisposeAsync().AsTask().Wait() twice, which froze the window on exit and risked a deadlock: session
        // disposal awaits the control keep-alive task and joins the decode worker.
        _ = TeardownAsync();
    }

    private async Task TeardownAsync()
    {
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
