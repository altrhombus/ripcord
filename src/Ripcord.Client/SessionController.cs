using Ripcord.Core.Input;
using Ripcord.Core.Power;
using Ripcord.Diagnostics;
using Ripcord.Core.Reactive;
using Ripcord.Core.Sessions;

namespace Ripcord.Client;

/// <summary>
/// Owns a streaming session's whole life: connect, watch, degrade, reconnect, tear down. This is the layer the
/// app was missing — <see cref="SessionState.Degraded"/> and <see cref="SessionState.Reconnecting"/> existed as
/// enum values that nothing ever assigned, so a Wi-Fi blip ended a session permanently and the only feedback
/// was a line of grey text.
///
/// <para>
/// Everything external is injected (session factory, sink, input, power, clock, delay), so the entire policy —
/// including backoff timing and stall handling — is unit-testable without a console, a GPU, or a real delay.
/// </para>
///
/// <para>
/// Threading: <see cref="StartAsync"/> launches one background loop. All state transitions happen on that loop
/// or under <see cref="_gate"/>; <see cref="Status"/> observers are notified from whichever thread caused the
/// change, so a UI consumer must marshal to its own dispatcher (the app layer does this).
/// </para>
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IStreamingSession>> _sessionFactory;
    private readonly IVideoDecodePipeline _pipeline;
    private readonly IObservable<ControllerStateFrame>? _controllerInput;
    private readonly IPowerThermalMonitor _power;
    private readonly SessionControllerOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly Subject<SessionStatus> _status = new();
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private SessionConfig? _config;

    // The live session and its subscriptions, replaced wholesale on each reconnect.
    private IStreamingSession? _session;
    private IDisposable? _videoSub;
    private IDisposable? _audioSub;
    private IDisposable? _inputSub;
    private IDisposable? _statsSub;

    private long _lastFrameTicks;      // UtcTicks of the newest video frame; 0 = none this session
    private long _connectedAtTicks;    // UtcTicks of the last successful handshake
    private SessionStatistics? _lastStats;

    /// <param name="sessionFactory">
    /// Opens a session, once per connect attempt including reconnects.
    ///
    /// <para>
    /// <b>Asynchronous, and it has to be.</b> A LAN session is a socket and could be handed over synchronously,
    /// but a session reached through the account rendezvous is not: it is a cloud round trip that takes tens of
    /// seconds before there is anything to hand back. A synchronous factory could only have blocked the caller
    /// or lied about being ready. The token is the connect's own, so a user who leaves the page mid-rendezvous
    /// cancels it rather than waiting it out.
    /// </para>
    ///
    /// <para>
    /// Whatever it returns is disposed by this controller and must own everything it needs — a route that holds
    /// extra machinery open (an association, a push channel) hands back a session that disposes them with
    /// itself, rather than expecting the controller to know they exist.
    /// </para>
    /// </param>
    public SessionController(
        Func<CancellationToken, Task<IStreamingSession>> sessionFactory,
        IVideoDecodePipeline pipeline,
        IObservable<ControllerStateFrame>? controllerInput = null,
        IPowerThermalMonitor? power = null,
        SessionControllerOptions? options = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _controllerInput = controllerInput;
        _power = power ?? new UnknownPowerThermalMonitor();
        _options = options ?? new SessionControllerOptions();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Current lifecycle state.</summary>
    public SessionLifecycle Lifecycle { get; private set; } = SessionLifecycle.Idle;

    /// <summary>Latest status, also pushed to <see cref="Status"/> observers on every change.</summary>
    public SessionStatus CurrentStatus { get; private set; } =
        new(SessionLifecycle.Idle, "Not connected.");

    /// <summary>Status changes. Late subscribers should read <see cref="CurrentStatus"/> for the current value.</summary>
    public IObservable<SessionStatus> Status => _status;

    /// <summary>Most recent wire statistics, or null before the first sample.</summary>
    public SessionStatistics? LastStatistics => _lastStats;

    /// <summary>
    /// Whether the session should ask the console to rest when it ends. The app sets this from the disconnect
    /// prompt just before tearing down; it forwards to the live session and is remembered so a later session
    /// (reconnect) inherits the choice rather than reverting to the connect-time default.
    /// </summary>
    public bool RestConsoleOnDisconnect
    {
        get
        {
            lock (_gate)
            {
                return _session?.RestConsoleOnDisconnect ?? _restConsoleOnDisconnect;
            }
        }
        set
        {
            lock (_gate)
            {
                _restConsoleOnDisconnect = value;
                if (_session is not null)
                {
                    _session.RestConsoleOnDisconnect = value;
                }
            }
        }
    }

    private bool _restConsoleOnDisconnect;

    /// <summary>
    /// While true, controller frames are dropped instead of forwarded to the console. Used when an in-app
    /// overlay — the disconnect prompt — takes the pad, so button presses drive the dialog rather than leaking
    /// into the game behind it. Setting it true also sends one neutral frame, so a gesture that was being held
    /// when the overlay opened (the exit combo) is released on the console rather than left stuck down.
    /// </summary>
    public bool SuspendInputForwarding
    {
        get => Volatile.Read(ref _inputSuspended) != 0;
        set
        {
            Volatile.Write(ref _inputSuspended, value ? 1 : 0);

            if (!value)
            {
                return;
            }

            IStreamingSession? session;
            lock (_gate)
            {
                session = _session;
            }

            try
            {
                session?.InputSink.SubmitControllerState(default);
            }
            catch (Exception)
            {
                // best-effort neutralization; a dropped release frame self-corrects on the next real one
            }
        }
    }

    private int _inputSuspended;

    /// <summary>
    /// How many statistics samples have arrived this session. Lets the UI distinguish "RTT is genuinely near
    /// zero" from "the RTT metric is not being fed", which an integer millisecond reading could not.
    /// </summary>
    public long StatisticsSampleCount => _statsSampleCount;

    private long _statsSampleCount;

    /// <summary>
    /// What the adaptive controller currently thinks the quality should be, or null with no live session.
    ///
    /// <para>
    /// This is a <em>recommendation</em>, and only partly actionable mid-session: the bitrate does reach the
    /// console via CONNECTION_QUALITY, but that message has no resolution or frame-rate field — those are fixed
    /// by the launchSpec at session start and can only change on a reconnect. So a recommended resolution below
    /// what is being received is expected, not a failure to apply it.
    /// </para>
    /// </summary>
    /// <summary>
    /// Why the controller last changed or capped quality, or empty with no live session. Paired with
    /// <see cref="RecommendedQuality"/>: a target that differs from the request is not self-explanatory, and an
    /// unattributed drop to 720p on a healthy link is indistinguishable from a bug.
    /// </summary>
    public string QualityReason
    {
        get
        {
            IStreamingSession? session;
            lock (_gate)
            {
                session = _session;
            }

            try
            {
                return session?.BandwidthController.LastDecisionReason ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }
    }

    public BitrateDecision? RecommendedQuality
    {
        get
        {
            IStreamingSession? session;
            lock (_gate)
            {
                session = _session;
            }

            try
            {
                return session?.BandwidthController.RecommendBitrate();
            }
            catch (InvalidOperationException)
            {
                return null; // torn down between the read and the call
            }
        }
    }

    /// <summary>Milliseconds since the newest video frame, or null if none has arrived this session.</summary>
    public double? MillisecondsSinceLastFrame
    {
        get
        {
            long ticks = Volatile.Read(ref _lastFrameTicks);
            return ticks == 0 ? null : (_clock().UtcTicks - ticks) / (double)TimeSpan.TicksPerMillisecond;
        }
    }

    /// <summary>Milliseconds since the last successful handshake, or 0 if never connected.</summary>
    public double MillisecondsSinceConnect
    {
        get
        {
            long ticks = Volatile.Read(ref _connectedAtTicks);
            return ticks == 0 ? 0 : (_clock().UtcTicks - ticks) / (double)TimeSpan.TicksPerMillisecond;
        }
    }

    /// <summary>Begin connecting and keep the session alive until stopped or the retry budget runs out.</summary>
    public Task StartAsync(SessionConfig config, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_runLoop is not null)
            {
                throw new InvalidOperationException("This controller has already been started.");
            }

            _config = config;
            _restConsoleOnDisconnect = config.RestConsoleOnDisconnect;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runLoop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <summary>Stop deliberately. Idempotent; safe to call from a UI teardown path.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _runLoop;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            // The loop swallows cancellation, so this completes rather than throwing.
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        await TeardownSessionAsync().ConfigureAwait(false);
        Transition(SessionLifecycle.Closed, "Disconnected.");
    }

    /// <summary>
    /// The lifecycle loop: connect, then watch until the session ends, then decide whether to reconnect.
    /// Structured as one loop rather than event callbacks so the order of transitions is obvious and testable.
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        int attempt = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool isRetry = attempt > 0;
                Transition(
                    isRetry ? SessionLifecycle.Reconnecting : SessionLifecycle.Connecting,
                    isRetry ? "Reconnecting to your console…" : "Connecting to your console…",
                    isRetry ? attempt : 0);

                ConnectOutcome outcome = await TryConnectAsync(cancellationToken).ConfigureAwait(false);

                if (outcome.Connected)
                {
                    Transition(SessionLifecycle.Streaming, "Connected.");
                    DateTimeOffset streamingSince = _clock();

                    // Returns when the session ends or stalls past the reconnect threshold.
                    await WatchSessionAsync(cancellationToken).ConfigureAwait(false);
                    await TeardownSessionAsync().ConfigureAwait(false);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // Only a session that LASTED resets the budget.
                    //
                    // This used to reset on connect, which made MaxReconnectAttempts unreachable in the one
                    // case that needs it. A console on its way into rest mode completes the handshake and
                    // drops it immediately, so every attempt counted as a success, the budget reset every
                    // time, and the loop ran forever. Observed on hardware: the console went back to sleep and
                    // the app reconnected indefinitely, with no way out but killing it.
                    if (_clock() - streamingSince >= _options.MinimumHealthySession)
                    {
                        attempt = 1;
                        continue;
                    }

                    attempt++;
                    if (attempt > _options.MaxReconnectAttempts)
                    {
                        Transition(
                            SessionLifecycle.Failed,
                            $"The console accepted {_options.MaxReconnectAttempts} connections and dropped each "
                            + "one immediately. It may be going into rest mode — wake it and try again.");
                        return;
                    }

                    // Backoff on a flap too. Reconnecting instantly into a console that is shutting down is
                    // what turned this into a tight loop rather than a slow one.
                    TimeSpan flapBackoff = BackoffFor(attempt);
                    Transition(
                        SessionLifecycle.Reconnecting,
                        $"The stream dropped straight away. Retrying in {Math.Ceiling(flapBackoff.TotalSeconds)}s…",
                        attempt,
                        flapBackoff);

                    try
                    {
                        await _delay(flapBackoff, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                await TeardownSessionAsync().ConfigureAwait(false);

                if (!outcome.Retryable)
                {
                    // Retrying an unretryable failure just burns time and hides the real problem.
                    Transition(SessionLifecycle.Failed, outcome.Detail);
                    return;
                }

                attempt++;
                if (attempt > _options.MaxReconnectAttempts)
                {
                    Transition(
                        SessionLifecycle.Failed,
                        $"Couldn't reconnect after {_options.MaxReconnectAttempts} attempts. {outcome.Detail}");
                    return;
                }

                TimeSpan backoff = BackoffFor(attempt);
                Transition(
                    SessionLifecycle.Reconnecting,
                    $"Connection failed. Retrying in {Math.Ceiling(backoff.TotalSeconds)}s… ({outcome.Detail})",
                    attempt,
                    backoff);

                try
                {
                    await _delay(backoff, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // deliberate stop
        }
        catch (Exception ex)
        {
            // The loop must never fault silently: that would leave the UI showing "Connecting…" forever.
            Transition(SessionLifecycle.Failed, $"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Exponential backoff for a given attempt, capped at <see cref="SessionControllerOptions.MaxBackoff"/>.
    /// Deterministic (no jitter) so the policy stays testable, and public so a UI can show the user how long
    /// the next attempt is away rather than leaving them guessing.
    /// </summary>
    public TimeSpan BackoffFor(int attempt)
    {
        double seconds = _options.InitialBackoff.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, _options.MaxBackoff.TotalSeconds));
    }

    private readonly record struct ConnectOutcome(bool Connected, bool Retryable, string Detail);

    /// <summary>Build a session, run the handshake, and wire up media/input/stats on success.</summary>
    private async Task<ConnectOutcome> TryConnectAsync(CancellationToken cancellationToken)
    {
        IStreamingSession session;
        try
        {
            session = await _sessionFactory(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Retryable: opening a session now reaches the network, so this covers a rendezvous that timed out
            // or a console that was busy -- transient things worth another attempt, where before it could only
            // be a construction error.
            return new ConnectOutcome(false, Retryable: true, $"Couldn't start a session: {ex.Message}");
        }

        lock (_gate)
        {
            _session = session;
        }

        try
        {
            SessionHandshakeResult result = await session
                .ConnectAsync(_config!, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                string reason = result.FailureReason ?? "unknown reason";
                return new ConnectOutcome(false, IsRetryable(reason), reason);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ConnectOutcome(false, Retryable: true, ex.Message);
        }

        Volatile.Write(ref _connectedAtTicks, _clock().UtcTicks);
        Volatile.Write(ref _lastFrameTicks, 0);
        Subscribe(session);
        return new ConnectOutcome(true, Retryable: true, "Connected.");
    }

    /// <summary>
    /// Whether a handshake failure is worth retrying. Conservative: retry unless the reason clearly indicates a
    /// problem repetition cannot fix (missing pairing, rejected credentials). A misclassified transient failure
    /// costs one wasted retry; a misclassified permanent one costs the user a minute of false hope.
    /// </summary>
    private static bool IsRetryable(string reason)
    {
        string[] permanent =
        [
            "not paired", "registration", "registkey", "credential", "unauthor",
            "rejected (403", "rejected (401", "control secrets",
        ];

        foreach (string marker in permanent)
        {
            if (reason.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Route media and input, and stamp frame arrivals for the watchdog. The controller sits between session and
    /// pipeline precisely so it can observe liveness without depending on the render stack.
    /// </summary>
    private void Subscribe(IStreamingSession session)
    {
        _videoSub = session.VideoFrames.Subscribe(new Sink<EncodedVideoFrame>(frame =>
        {
            Volatile.Write(ref _lastFrameTicks, _clock().UtcTicks);
            _pipeline.SubmitEncodedVideo(frame);
        }));

        _audioSub = session.AudioFrames.Subscribe(new Sink<EncodedAudioFrame>(_pipeline.SubmitEncodedAudio));

        _statsSub = session.Statistics.Subscribe(new Sink<SessionStatistics>(s =>
        {
            _lastStats = s;
            Interlocked.Increment(ref _statsSampleCount);
            RipcordEventSource.Log.NetworkSample(s.BitrateKbps, s.PacketLossRatio, s.RoundTripTimeMs, s.ReceiveQueueDepth);
            TraceQualityChanges(session);
        }));

        if (_controllerInput is not null)
        {
            _inputSub = _controllerInput.Subscribe(new Sink<ControllerStateFrame>(frame =>
            {
                // Dropped, not queued, while an overlay owns the pad — see SuspendInputForwarding.
                if (Volatile.Read(ref _inputSuspended) == 0)
                {
                    session.InputSink.SubmitControllerState(frame);
                }
            }));
        }

        // Hand the session's rate controller the device's power/thermal state, and keep it current. Nothing
        // supplied this before, so quality was identical on mains power and on a throttling handheld.
        session.BandwidthController.ReportThermalPowerState(_power.Current);
        _power.Changed += OnPowerChanged;

        // Close the resynchronisation loop: the decoder decides it cannot catch up, the session asks the
        // console for a fresh IDR. The controller is the only place that knows about both.
        _pipeline.KeyFrameRequested += OnKeyFrameRequested;
    }

    /// <summary>
    /// Emit a trace event when the bandwidth controller's decision changes.
    ///
    /// <para>
    /// Deliberately here rather than inside the controller itself: Ripcord.Core has no project references at
    /// all, and that is worth protecting — it is the assembly a second front end links, and the whole reason the
    /// core builds and tests away from Windows. One trace call is not worth giving it a dependency, so the
    /// observability layer observes instead.
    /// </para>
    /// </summary>
    private void TraceQualityChanges(IStreamingSession session)
    {
        try
        {
            BitrateDecision decision = session.BandwidthController.RecommendBitrate();
            if (decision == _lastTracedQuality)
            {
                return;
            }

            _lastTracedQuality = decision;
            RipcordEventSource.Log.QualityChanged(
                decision.Width,
                decision.Height,
                decision.Fps,
                decision.BitrateKbps,
                session.BandwidthController.LastDecisionReason);
        }
        catch (InvalidOperationException)
        {
            // Session torn down between the sample and the read.
        }
    }

    private BitrateDecision? _lastTracedQuality;

    private void OnKeyFrameRequested()
    {
        IStreamingSession? session;
        lock (_gate)
        {
            session = _session;
        }

        RipcordEventSource.Log.KeyFrameRequested("decoder backlog resynchronisation");

        try
        {
            session?.RequestKeyFrame();
        }
        catch (Exception)
        {
            // Best-effort recovery hint; never let it disturb the decode path.
        }
    }

    private void OnPowerChanged(PowerState state)
    {
        IStreamingSession? session;
        lock (_gate)
        {
            session = _session;
        }

        try
        {
            session?.BandwidthController.ReportThermalPowerState(state);
        }
        catch (InvalidOperationException)
        {
            // Session torn down between the read and the call; nothing to report to.
        }
    }

    /// <summary>
    /// Watch a live session. Returns when it needs replacing — either the session closed itself, or video has
    /// been absent long enough that reconnecting is the better bet.
    /// </summary>
    private async Task WatchSessionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _delay(_options.WatchdogInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            IStreamingSession? session;
            lock (_gate)
            {
                session = _session;
            }

            if (session is null || session.State == SessionState.Closed)
            {
                return; // the session gave up on its own; the loop decides whether to reconnect
            }

            double sinceFrame = SinceLastFrameOrConnectMs();

            // A stalled PICTURE is not the same as a stalled SESSION. A completely static scene — a pause menu with
            // no animation — gives the encoder nothing to send, so frames legitimately stop while the console is
            // healthy and still talking to us. Escalating on frames alone would show "Video has stopped" and then
            // tear down a perfectly good session because the user paused their game.
            //
            // Console activity is the discriminator: our own congestion loop is timer-driven and keeps producing
            // statistics regardless, so only inbound datagrams prove the far end is alive.
            double? sinceConsole = session.MillisecondsSinceConsoleActivity;
            bool consoleStillTalking =
                sinceConsole is double quiet && quiet < _options.StallTimeout.TotalMilliseconds;

            if (consoleStillTalking)
            {
                // Healthy but idle. Recover the lifecycle if a previous dip had degraded it, then keep watching.
                //
                // CONTINUE, not return: returning exits this watchdog loop, which is precisely how the outer
                // session loop is told to replace the session — so an early `return` here caused the reconnect it
                // was written to prevent. The tests caught it; the two exits differ by one keyword and mean
                // opposite things.
                if (Lifecycle == SessionLifecycle.Degraded)
                {
                    Transition(SessionLifecycle.Streaming, "Connected.");
                }

                continue;
            }

            if (sinceFrame >= _options.ReconnectAfterStall.TotalMilliseconds)
            {
                // Long enough that this is not a dropped IDR — replace the session.
                return;
            }

            if (sinceFrame >= _options.StallTimeout.TotalMilliseconds)
            {
                if (Lifecycle != SessionLifecycle.Degraded)
                {
                    Transition(SessionLifecycle.Degraded, "Video has stopped — trying to recover…");
                }
            }
            else if (Lifecycle == SessionLifecycle.Degraded)
            {
                // Recovered without a reconnect, which is the common case for a brief dip.
                Transition(SessionLifecycle.Streaming, "Connected.");
            }
        }
    }

    /// <summary>
    /// Time to measure a stall against: since the last frame, or — before any frame has arrived — since the
    /// handshake, so a session that connects but never delivers video is still caught.
    /// </summary>
    private double SinceLastFrameOrConnectMs()
    {
        long frameTicks = Volatile.Read(ref _lastFrameTicks);
        long baseline = frameTicks != 0 ? frameTicks : Volatile.Read(ref _connectedAtTicks);
        return baseline == 0 ? 0 : (_clock().UtcTicks - baseline) / (double)TimeSpan.TicksPerMillisecond;
    }

    private async Task TeardownSessionAsync()
    {
        IStreamingSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        _power.Changed -= OnPowerChanged;
        _pipeline.KeyFrameRequested -= OnKeyFrameRequested;

        _inputSub?.Dispose();
        _videoSub?.Dispose();
        _audioSub?.Dispose();
        _statsSub?.Dispose();
        _inputSub = _videoSub = _audioSub = _statsSub = null;

        if (session is not null)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* teardown races are not worth failing a reconnect over */ }
        }
    }

    private void Transition(SessionLifecycle lifecycle, string detail, int attempt = 0, TimeSpan? nextRetry = null)
    {
        Lifecycle = lifecycle;
        var status = new SessionStatus(lifecycle, detail, attempt, nextRetry);
        CurrentStatus = status;

        // Trace every transition. A reconnect storm or a stall/recover cycle is invisible in an instantaneous UI
        // but obvious in a capture timeline.
        RipcordEventSource.Log.SessionStateChanged(lifecycle.ToString(), detail);
        if (lifecycle == SessionLifecycle.Failed)
        {
            RipcordEventSource.Log.SessionFailed(detail);
        }
        else if (lifecycle == SessionLifecycle.Reconnecting && nextRetry is { } delay)
        {
            RipcordEventSource.Log.ReconnectScheduled(attempt, delay.TotalMilliseconds, detail);
        }

        _status.OnNext(status);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _runLoop = null;
        }

        _status.OnCompleted();
    }

    private sealed class Sink<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
