using Ripcord.Client;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Diagnostics;
using Ripcord.Presentation.Threading;
using Ripcord.Presentation.Resources;

namespace Ripcord.Presentation.Sessions;

/// <summary>
/// What the streaming surface says, as opposed to what it draws.
///
/// <para>
/// Extracted from <c>SessionPage</c>, where the status overlay's wording and the diagnostics readout's ~150
/// lines of formatting, arithmetic and threshold judgement were interleaved with D3D12 device creation, swap
/// chain resizing and pointer handlers. Nothing in here needs a GPU, a console or a window, and several of the
/// rules it encodes were previously only observable by connecting to real hardware and watching:
/// </para>
///
/// <list type="bullet">
///   <item>the sampling-interval floor that stops a delayed timer tick reporting an impossible frame rate;</item>
///   <item>"resolution pending" versus "resolution unknown", which are different faults with the same symptom;</item>
///   <item>when the adaptive-quality row has anything to say, and when it is noise;</item>
///   <item>which of three reasons the picture is smaller than requested.</item>
/// </list>
///
/// <para>
/// What deliberately stays behind: the device itself (via <see cref="IVideoPipelineStats"/>), the swap chain,
/// the fullscreen presenter, keep-display-awake, input routing and the connected animation. The line is the one
/// Stage A draws generally — anything producing a string, bool or enum comes up here; anything touching a
/// device, a presenter or a native handle does not.
/// </para>
/// </summary>
public sealed class SessionViewModel : ObservableState<SessionViewState>
{
    private readonly IVideoPipelineStats _pipeline;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Decides which health verdicts are worth interrupting the player about. See the class note.</summary>
    private readonly HealthAlertGate _alertGate = new();

    private bool _alertRaised;

    private DiagnosticsRung _rung;

    private ConnectPhase? _phase;

    private DateTimeOffset? _traceStartedAt;

    /// <summary>
    /// Which rung to come back to. Someone who lives at rung 3 gets rung 3, which is the whole reason the
    /// key is a toggle rather than a cycle: it returns you where you were, not one step further in.
    /// </summary>
    private DiagnosticsRung _lastShown = DiagnosticsRung.Summary;

    private RipcordSettings _settings;

    // ---- overlay ----
    private bool _statusVisible = true;
    private string _statusHeadline = Strings.Session_Starting;
    private string _statusDetail = string.Empty;
    private bool _statusBusy = true;
    private bool _statusTerminal;
    private bool _isStreamLive;

    // ---- diagnostics ----
    private SessionDiagnosticsState _diagnostics = SessionDiagnosticsState.Empty;

    // Rate sampling. Cumulative counters differenced against the previous sample, so the interval matters.
    private long _prevDecodedFrames;
    private long _prevPresentedFrames;
    private long _prevAudioFrames;
    private long _prevSampleTicks;

    // Held so an unchanged pill set keeps the SAME list instance across recompositions. Record equality is
    // reference equality for a list, so a fresh list each tick would make every state "changed" and notify
    // twice a second forever — the exact binding churn ObservableState's value-equality check exists to avoid.
    private IReadOnlyList<CapabilityPill> _pills = [];
    private string _pillSignature = string.Empty;

    // Every currently-attached pad, keyed by id. A set rather than "last event wins", because every input engine
    // runs at once and several pads can be attached — which of them are contributing is otherwise invisible.
    private readonly Dictionary<string, string> _controllers = [];

    /// <summary>
    /// Shortest sampling interval that yields a meaningful rate. The stats timer nominally fires every 500 ms, so
    /// anything under half that is a bunched tick after a delay rather than a real observation window — dividing a
    /// burst of frames by a ~37 ms gap reported 1610 fps, which is impossible, and it poisoned the 30-second peak
    /// permanently because a maximum never decays. The sample is skipped rather than clamped, so the next interval
    /// is measured from the last GOOD sample and the frames are still counted, just over an honest window.
    /// </summary>
    public static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMilliseconds(250);

    public SessionViewModel(
        IVideoPipelineStats pipeline,
        RipcordSettings settings,
        IUiDispatcher dispatcher,
        Func<DateTimeOffset>? clock = null)
        : base(dispatcher)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        // The setting is "how much of the HUD is up when a stream starts", so it is read once here rather
        // than watched: changing it mid-session and having the panel appear over the game would be a
        // surprise, and the key that shows it is one press away regardless.
        _rung = _settings.DiagnosticsRungOnConnect;
    }

    /// <summary>
    /// Rolling history for the sparklines. 60 samples at the 500 ms cadence is 30 seconds.
    ///
    /// <para>
    /// Exposed rather than folded into the state record because a front end plots these by walking the samples,
    /// and copying 240 doubles into an immutable record twice a second to hand back the same numbers would be
    /// pure ceremony. <see cref="MetricHistory"/> is already a portable type.
    /// </para>
    /// </summary>
    public MetricHistory FpsHistory { get; } = new(60);

    /// <summary>
    /// The most recent sample as raw numbers, for the session trace. Null until a live sample has been
    /// taken. Outside the state record for the same reason the histories are: the front end reads it on
    /// the tick that produced it, and copying it into an immutable record twice a second to hand back the
    /// same values would be ceremony.
    /// </summary>
    public SessionSample? LastSample { get; private set; }

    public MetricHistory LossHistory { get; } = new(60);

    public MetricHistory RttHistory { get; } = new(60);

    public MetricHistory BitrateHistory { get; } = new(60);

    /// <summary>
    /// Full scale for the bitrate plot: a little above the configured cap, so a stream sitting at its ceiling
    /// draws near the top rather than off it. No threshold rule — bitrate is a magnitude, not a verdict.
    /// </summary>
    public double BitrateFullScaleMbps => Math.Max(1, _settings.BitrateKbps / 1000.0 * 1.2);

    /// <summary>The bitrate plot. Never carries a severity: there is no bitrate that is wrong by itself.</summary>
    public MetricPlot BitratePlot => new(BitrateFullScaleMbps, WarnFraction: null);

    /// <summary>
    /// The frame-rate plot. Scaled to the requested rate with headroom, so "at target" sits high but not
    /// clipped and a shortfall reads as a drop. This is the one plot whose threshold is not its ceiling, so
    /// the rule is drawn where a shortfall actually begins.
    /// </summary>
    public MetricPlot FramesPlot => new(
        FramesFullScale,
        WarnFraction: StreamHealthAssessor.FpsShortfallFactor / FramesHeadroom);

    /// <summary>The latency plot. Full scale is the warn threshold, so the rule sits along the top.</summary>
    public static MetricPlot LatencyPlot { get; } = new(RttFullScaleMs, WarnFraction: 1);

    /// <summary>The loss plot. Full scale is the warn threshold, so the rule sits along the top.</summary>
    public static MetricPlot LossPlot { get; } = new(LossFullScalePercent, WarnFraction: 1);

    /// <summary>Headroom above the requested frame rate, so a stream exactly at target is not drawn clipped.</summary>
    private const double FramesHeadroom = 1.2;

    private double FramesFullScale => Math.Max(1, _settings.TargetFps * FramesHeadroom);

    /// <summary>
    /// Full scale for the loss plot: the health assessor's warn threshold, so the line touching the top means
    /// exactly "loss is now a problem". It was 20%, against which a real 0.4% blip drew as 2% of the row height —
    /// i.e. invisible, so the row could not distinguish "no loss" from "a little loss", which is the distinction
    /// that matters most on a wireless link.
    /// </summary>
    public static double LossFullScalePercent => StreamHealthAssessor.LossWarnRatio * 100.0;

    /// <summary>Full scale for the latency plot, on the same principle as loss.</summary>
    public static double RttFullScaleMs => StreamHealthAssessor.RttWarnMs;

    // ---- transitions ---------------------------------------------------------------------------

    /// <summary>
    /// Show the HUD, or hide it. <b>It never reveals more.</b>
    ///
    /// <para>
    /// Toggling is the learned convention for a debug overlay everywhere else, so a key that cycled deeper on
    /// its second press would do the opposite of what the muscle expects - and the failure is the worst one
    /// available: MORE of the game covered at the moment someone was trying to uncover it. Going deeper is a
    /// visible control instead, which is also what makes rung 3 discoverable; a hidden second keypress is not
    /// an affordance.
    /// </para>
    /// </summary>
    public void ToggleDiagnostics() => Mutate(() =>
    {
        if (_rung == DiagnosticsRung.Hidden)
        {
            _rung = _lastShown;
        }
        else
        {
            _lastShown = _rung;
            _rung = DiagnosticsRung.Hidden;
        }
    });

    /// <summary>Go one rung deeper, from the summary strip to the full instrument panel.</summary>
    public void ShowDiagnosticsDetail() => Mutate(() => _rung = DiagnosticsRung.Full);

    /// <summary>Come back up to the summary strip.</summary>
    public void HideDiagnosticsDetail() => Mutate(() => _rung = DiagnosticsRung.Summary);

    /// <summary>Settings can change under a live session (the user has another window open).</summary>
    public void UseSettings(RipcordSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Mutate(() => _settings = settings);
    }

    /// <summary>
    /// Say what is happening. <paramref name="terminal"/> means the session will not leave this state on its
    /// own, so the busy indicator goes away and the user is offered something to do.
    /// </summary>
    public void ShowStatus(string headline, string detail, bool terminal) => Mutate(() =>
    {
        _statusVisible = true;
        _statusHeadline = headline ?? string.Empty;
        _statusDetail = detail ?? string.Empty;
        _statusBusy = !terminal;
        _statusTerminal = terminal;

        // Cleared, not kept. This overload is what a reconnect, a stall and a close all go through, and a
        // trail still sitting at "waking" during a reconnect is claiming progress that belongs to a
        // sequence which already finished.
        _phase = null;
    });

    /// <summary>
    /// Show one step of the connect sequence. Separate from <see cref="ShowStatus"/> because only these
    /// carry a phase — see the note on <see cref="SessionViewState.Phase"/>.
    /// </summary>
    public void ShowConnectStage(ConnectStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);

        Mutate(() =>
        {
            _statusVisible = true;
            _statusHeadline = stage.Headline;
            _statusDetail = stage.Detail;
            _statusBusy = !stage.Terminal;
            _statusTerminal = stage.Terminal;
            _phase = stage.Phase;
        });
    }

    public void HideStatus() => Mutate(() =>
    {
        _statusVisible = false;
        _statusBusy = false;
    });

    /// <summary>
    /// Translate a lifecycle change into what the overlay says.
    ///
    /// <para>
    /// Note what <see cref="SessionLifecycle.Degraded"/> does <em>not</em> do: most stalls recover in under a
    /// second, and flashing a frightening overlay over a picture that is about to come back is worse than saying
    /// nothing loudly. It reports quietly and keeps the stream considered live.
    /// </para>
    /// </summary>
    public void ApplyLifecycle(SessionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        switch (status.Lifecycle)
        {
            case SessionLifecycle.Streaming:
                Mutate(() =>
                {
                    _isStreamLive = true;
                    _statusVisible = false;
                    _statusBusy = false;
                });
                break;

            case SessionLifecycle.Degraded:
                ShowStatus(Strings.Session_ReconnectingVideo, status.Detail, terminal: false);
                break;

            case SessionLifecycle.Connecting:
            case SessionLifecycle.Reconnecting:
                // A fresh attempt starts with nothing sustained, so the previous session's notice cannot
                // survive into a stream that has not been measured yet.
                Mutate(() =>
                {
                    _alertGate.Reset();
                    _alertRaised = false;
                });
                ShowStatus(
                    status.Lifecycle == SessionLifecycle.Connecting
                        ? Strings.Session_Connecting
                        : Strings.Session_Reconnecting,
                    status.Detail,
                    terminal: false);
                break;

            case SessionLifecycle.Failed:
                Mutate(() =>
                {
                    _isStreamLive = false;
                    _statusVisible = true;
                    _statusHeadline = Strings.Session_CouldNotConnect;
                    _statusDetail = status.Detail ?? string.Empty;
                    _statusBusy = false;
                    _statusTerminal = true;
                });
                break;

            case SessionLifecycle.Closed:
                Mutate(() =>
                {
                    _isStreamLive = false;
                    _statusVisible = false;
                    _statusBusy = false;
                });
                break;
        }
    }

    /// <summary>
    /// A pad was attached or detached.
    ///
    /// <para>
    /// Records the engine, the transport and the device id, not merely "connected" — the source event carries
    /// all three and they were previously discarded, which made a hardware test inconclusive.
    /// </para>
    ///
    /// <para>
    /// Takes the facts rather than the front end's connection event, because that type lives in the Windows-only
    /// input assembly. The front end maps its own transport enum onto <see cref="ControllerLink"/>; everything
    /// after that — tracking the set, and what the label says for none, one or several — is here.
    /// </para>
    /// </summary>
    public void ApplyControllerConnection(string controllerId, bool connected, ControllerLink link, string? engine)
    {
        ArgumentException.ThrowIfNullOrEmpty(controllerId);

        Mutate(() =>
        {
            if (!connected)
            {
                _controllers.Remove(controllerId);
                return;
            }

            string transport = link switch
            {
                ControllerLink.Usb => "USB",
                ControllerLink.Bluetooth => "Bluetooth",
                _ => "transport n/a",
            };

            _controllers[controllerId] =
                $"{controllerId} · {transport} · {(string.IsNullOrEmpty(engine) ? "?" : engine)}";
        });
    }

    /// <summary>
    /// Take one diagnostics sample.
    ///
    /// <para>
    /// Returns false when the sample was skipped — either because there is no pipeline to read yet, or because
    /// too little time has passed since the last one for a rate to mean anything. A caller that redraws graphs
    /// can use that to skip the redraw too.
    /// </para>
    /// </summary>
    public bool Sample(SessionTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        if (!_pipeline.IsReady)
        {
            return false;
        }

        VideoPipelineSnapshot s = _pipeline.Read();
        DateTimeOffset now = _clock();
        long nowTicks = now.UtcTicks;
        double seconds = _prevSampleTicks == 0
            ? 0
            : (nowTicks - _prevSampleTicks) / (double)TimeSpan.TicksPerSecond;

        if (_prevSampleTicks != 0 && seconds < MinimumSampleInterval.TotalSeconds)
        {
            return false;
        }

        double decodeFps = seconds > 0 ? (s.DecodedFrames - _prevDecodedFrames) / seconds : 0;
        double presentFps = seconds > 0 ? (s.PresentedFrames - _prevPresentedFrames) / seconds : 0;
        double audioFps = seconds > 0 ? (s.AudioFramesDecoded - _prevAudioFrames) / seconds : 0;

        _prevDecodedFrames = s.DecodedFrames;
        _prevPresentedFrames = s.PresentedFrames;
        _prevAudioFrames = s.AudioFramesDecoded;
        _prevSampleTicks = nowTicks;

        SessionStatistics stats = telemetry.Statistics;

        if (telemetry.HasSession)
        {
            // Recorded BEFORE composing, so the newest sample is in the history the front end is about to plot.
            FpsHistory.Add(presentFps);
            LossHistory.Add(stats.PacketLossRatio * 100.0);
            RttHistory.Add(stats.RoundTripTimeMs);
            BitrateHistory.Add(stats.BitrateKbps / 1000.0);

            // The same numbers, unformatted, for the trace. Built here rather than in the front end because
            // every one of them has already been computed at this point, and a second implementation of
            // "frames over the interval" is a second chance to disagree with the panel.
            _traceStartedAt ??= now;
            LastSample = new SessionSample(
                ElapsedSeconds: (now - _traceStartedAt.Value).TotalSeconds,
                PresentFps: presentFps,
                DecodeFps: decodeFps,
                LossPercent: stats.PacketLossRatio * 100.0,
                RttMs: stats.RoundTripTimeMs,
                BitrateMbps: stats.BitrateKbps / 1000.0,
                ReceiveQueueDepth: stats.ReceiveQueueDepth,
                DecodeQueueDepth: s.QueueDepth,
                PipelineLatencyMs: s.PipelineLatencyMs,
                DecodeMode: s.DecodeMode,
                HealthLevel: _diagnostics.HealthLevel);
        }

        Mutate(() =>
        {
            _diagnostics = ComposeDiagnostics(s, telemetry, decodeFps, presentFps, audioFps);

            // The verdict is recomputed twice a second and is right to be twitchy - the panel wants the live
            // value. The notice over the game is not: the gate is what stops a two-second wobble becoming a
            // banner, and a banner nobody trusts is worse than none.
            _alertRaised = _alertGate.Update(_diagnostics.HealthLevel, now);
        });
        return telemetry.HasSession;
    }

    // ---- diagnostics composition ---------------------------------------------------------------

    private SessionDiagnosticsState ComposeDiagnostics(
        VideoPipelineSnapshot s,
        SessionTelemetry telemetry,
        double decodeFps,
        double presentFps,
        double audioFps)
    {
        SessionStatistics stats = telemetry.Statistics;

        UpdatePills(s);

        // "pending" and "the decoder never told us" are different problems: the second renders a black screen
        // while every counter looks healthy, so it must not hide behind a word that means "wait a moment".
        string resolution = (s.DecodedWidth, s.DecodedHeight, s.DecodedFrames) switch
        {
            ( > 0, > 0, _) => $"{s.DecodedWidth}×{s.DecodedHeight}",
            (_, _, > 0) => "resolution unknown",
            _ => "resolution pending",
        };

        // The raw counters are shown only while the decoded size is unknown — i.e. exactly when nothing is
        // reaching the screen and the ordinary readouts all look healthy. Noise the rest of the time.
        bool geometryUnknown = s.DecodedWidth == 0 || s.DecodedHeight == 0;
        string decoder = string.IsNullOrEmpty(s.Decoder)
            ? "—"
            : geometryUnknown && !string.IsNullOrEmpty(s.DecoderDiagnostic)
                ? $"{s.Decoder}\n{s.DecoderDiagnostic}"
                : s.Decoder;

        (string adaptive, bool adaptiveVisible) = ComposeAdaptive(telemetry);
        string reason = ComposeReason(s, telemetry, adaptiveVisible);

        // "probed" vs "assumed" is the whole point of the senkusha work: one is a measurement of the real path
        // in both directions, the other is the local interface's MTU minus an allowance.
        string declaredRtt = stats.DeclaredRttMs is double d ? $"{d:F1} ms" : "not measured";
        string link = stats.DeclaredMtu > 0
            ? $"MTU {stats.DeclaredMtu} ({(stats.MtuConfirmed ? "probed" : "assumed")}) · handshake {declaredRtt}"
            : "—";

        // Headroom against the cap, which is the question two bare numbers never answered.
        double capMbps = Math.Max(0.1, _settings.BitrateKbps / 1000.0);
        double usedFraction = Math.Clamp(stats.BitrateKbps / 1000.0 / capMbps, 0, 1);

        (string health, string healthTip, StreamHealthLevel level) =
            ComposeHealth(s, telemetry, decodeFps, presentFps);

        return new SessionDiagnosticsState(
            Adapter: _pipeline.AdapterDescription,

            Colour: Dash(s.ColorMatrix),
            VideoFormat: Dash(s.VideoFormat),
            HdrOutput: Dash(s.HdrOutput),
            HeroResolution: resolution.StartsWith("resolution", StringComparison.Ordinal) ? "—" : resolution,
            HeroFps: $"{presentFps:F0}",
            HeroBitrate: stats.BitrateKbps > 0 ? $"{stats.BitrateKbps / 1000.0:F1}" : "—",
            CapabilityPills: _pills,
            CapabilityPillSignature: _pillSignature,
            Decoder: decoder,

            Requested: $"{_settings.Width}×{_settings.Height} @ {_settings.TargetFps}",
            Adaptive: adaptive,
            AdaptiveVisible: adaptiveVisible,
            Reason: reason,
            ReasonVisible: !string.IsNullOrWhiteSpace(reason),

            Link: link,
            HeadroomUsedFraction: usedFraction,
            Headroom: $"{usedFraction * 100:F0}% of {capMbps:F0} Mbps",
            Power: ComposePower(telemetry.Power),

            // The frame rate is the useful part: 480 samples at 48 kHz means ~100/s, so a figure well below that
            // is audio falling behind, and zero is audio stopped — neither of which is audible as such.
            Audio: s.AudioFramesDecoded == 0 && s.AudioFramesSkipped == 0
                ? s.AudioFormat
                : $"{s.AudioFormat} · {audioFps:F0}/s"
                  + (s.AudioFramesSkipped > 0 ? $" · {s.AudioFramesSkipped} skipped" : string.Empty),

            // rtt and loss are deliberately absent here: each has its own labelled sparkline with a value and a
            // peak, and repeating them made this a wall of text.
            Decode: $"{decodeFps:F0} fps · {s.PipelineLatencyMs:F0} ms end to end",
            Queues: $"decode {s.QueueDepth} · receive {stats.ReceiveQueueDepth}",
            Path: DescribeDecodePath(s.DecodeMode),

            Health: health,
            HealthTip: healthTip,
            HealthLevel: level,
            HeroLoss: $"{stats.PacketLossRatio * 100:F1}%",
            VideoWidth: s.DecodedWidth,
            VideoHeight: s.DecodedHeight,

            FramesSeverity: FramesSeverityFor(presentFps),
            LatencySeverity: LatencySeverityFor(stats.RoundTripTimeMs),
            LossSeverity: LossSeverityFor(stats.PacketLossRatio));
    }

    /// <summary>
    /// Where the frame rate stands. Warning only: there is no "catastrophically low but still arriving" frame
    /// rate that is worse than the shortfall the assessor already names, and a stream with no frames at all is
    /// a verdict about the stream, not about this row.
    /// </summary>
    private MetricSeverity FramesSeverityFor(double presentFps)
        => presentFps < _settings.TargetFps * StreamHealthAssessor.FpsShortfallFactor
            ? MetricSeverity.Warning
            : MetricSeverity.Normal;

    /// <summary>
    /// Where latency stands. Thresholds come from the assessor rather than being restated here: the row and
    /// the verdict must agree, and two copies of a threshold is how they stop agreeing.
    /// </summary>
    private static MetricSeverity LatencySeverityFor(double rttMs) => rttMs switch
    {
        >= StreamHealthAssessor.RttBadMs => MetricSeverity.Critical,
        >= StreamHealthAssessor.RttWarnMs => MetricSeverity.Warning,
        _ => MetricSeverity.Normal,
    };

    /// <summary>Where loss stands, on the assessor's thresholds for the same reason as latency.</summary>
    private static MetricSeverity LossSeverityFor(double lossRatio) => lossRatio switch
    {
        >= StreamHealthAssessor.LossBadRatio => MetricSeverity.Critical,
        >= StreamHealthAssessor.LossWarnRatio => MetricSeverity.Warning,
        _ => MetricSeverity.Normal,
    };

    /// <summary>
    /// Which capabilities the picture currently has.
    ///
    /// <para>
    /// Real flags, never substring matches. A previous version tested the decoder description for "PQ", which is
    /// present whenever the <em>stream</em> is PQ — so the HDR pill lit even while the driver was tone-mapping to
    /// SDR on an SDR display, claiming a capability that was not in effect.
    /// </para>
    /// </summary>
    private void UpdatePills(VideoPipelineSnapshot s)
    {
        var pills = new List<CapabilityPill>(3);

        if (s.IsHdrOutput)
        {
            pills.Add(new CapabilityPill("HDR", Accent: true));
        }
        else if (s.IsTenBit)
        {
            // 10-bit without HDR is still worth surfacing: it is the gradient-precision win on its own.
            pills.Add(new CapabilityPill("10-bit", Accent: false));
        }

        if ((s.Decoder ?? string.Empty).Contains("HEVC", StringComparison.OrdinalIgnoreCase))
        {
            pills.Add(new CapabilityPill("HEVC", Accent: false));
        }

        if (s.DecodeMode == 2)
        {
            pills.Add(new CapabilityPill("Zero-copy", Accent: false));
        }

        string signature = string.Join("|", pills.Select(p => p.Label + (p.Accent ? "*" : string.Empty)));
        if (signature == _pillSignature)
        {
            return; // keep the existing instance — see the field's note
        }

        _pillSignature = signature;
        _pills = pills;
    }

    /// <summary>
    /// The adaptive-quality row, which appears only when the controller wants something other than what was
    /// asked for. Saying "adaptive: exactly what you configured" every half second is noise.
    /// </summary>
    private (string Text, bool Visible) ComposeAdaptive(SessionTelemetry telemetry)
    {
        if (!_settings.AdaptiveQuality
            || telemetry.RecommendedQuality is not { } t
            || t.BitrateKbps == _settings.BitrateKbps)
        {
            return (string.Empty, false);
        }

        // Say what is actually transmitted. CONNECTION_QUALITY carries the target BITRATE only — it has no
        // resolution field, and resolution is fixed by the launchSpec at session start — so a preferred
        // resolution is reported as needing a reconnect rather than implied to be in effect.
        string disposition = _settings.ReportConnectionQuality ? "sent to console" : "not sent (reporting off)";
        string line = $"{t.BitrateKbps / 1000.0:F1} Mbps · {disposition}";

        if (t.Width != _settings.Width || t.Height != _settings.Height || t.Fps != _settings.TargetFps)
        {
            line += $"\nprefers {t.Width}×{t.Height}@{t.Fps} — needs a reconnect";
        }

        return (line, true);
    }

    /// <summary>
    /// Why the picture is not what was asked for. Covers the controller's own reason, the console choosing a
    /// lower rung to fit the bitrate budget, and the decoder failing to report a frame size at all.
    /// </summary>
    private string ComposeReason(VideoPipelineSnapshot s, SessionTelemetry telemetry, bool adaptiveVisible)
    {
        // A decoder that reports frames but no geometry outranks everything else here: it is the one case where
        // the screen is black while every other number looks healthy.
        if (s.DecodedWidth == 0 && s.DecodedFrames > 0)
        {
            return "decoder reported no frame size — see the video row below";
        }

        string why = adaptiveVisible ? telemetry.QualityReason ?? string.Empty : string.Empty;

        if (s.DecodedHeight > 0 && s.DecodedHeight < _settings.Height)
        {
            string chose = $"console chose {s.DecodedWidth}×{s.DecodedHeight} to fit the "
                           + $"{_settings.BitrateKbps / 1000.0:F0} Mbps cap — raise it for more";
            why = string.IsNullOrWhiteSpace(why) ? chose : $"{why}\n{chose}";
        }

        return why;
    }

    private (string Health, string Tip, StreamHealthLevel Level) ComposeHealth(
        VideoPipelineSnapshot s,
        SessionTelemetry telemetry,
        double decodeFps,
        double presentFps)
    {
        if (!telemetry.HasSession)
        {
            // The assessor's inputs are all zero without a session, and "frame rate is below target" is a
            // misleading thing to say about a stream that has not started.
            return (Strings.Session_NotConnectedYet, Strings.Session_WaitingToStart, StreamHealthLevel.Info);
        }

        SessionStatistics stats = telemetry.Statistics;

        StreamHealthVerdict verdict = StreamHealthAssessor.Assess(new StreamHealthSignals(
            DecodeFps: decodeFps,
            PresentFps: presentFps,
            TargetFps: _settings.TargetFps,
            ReceiveQueueDepth: stats.ReceiveQueueDepth,
            DecodeQueueDepth: s.QueueDepth,
            PipelineLatencyMs: s.PipelineLatencyMs,
            DecodeMode: s.DecodeMode,
            PacketLossRatio: stats.PacketLossRatio,
            HasReceivedFrames: telemetry.MillisecondsSinceLastFrame is not null,
            MillisecondsSinceConnect: telemetry.MillisecondsSinceConnect,
            MillisecondsSinceLastFrame: telemetry.MillisecondsSinceLastFrame ?? 0,
            RoundTripTimeMs: stats.RoundTripTimeMs));

        return (verdict.Headline, verdict.Tip, verdict.Level);
    }

    /// <summary>
    /// Power state, reported as the monitor sees it — so a device cap is visibly attributable rather than
    /// looking like an unexplained quality drop.
    /// </summary>
    private static string ComposePower(PowerState? power) => power is null
        ? string.Empty
        : (power.Source == PowerSource.Battery ? "battery" : "mains")
          + (power.BatteryPercent is int pct ? $" {pct}%" : string.Empty)
          + (power.EnergySaverActive ? " · energy saver" : string.Empty)
          + (power.BatteryCritical ? " · critical" : string.Empty)
          + (power.ThermalThrottling ? " · throttling" : string.Empty);

    private static string DescribeDecodePath(int decodeMode) => decodeMode switch
    {
        2 => "zero-copy",
        1 => "readback",
        _ => "software",
    };

    private static string Dash(string? value) => string.IsNullOrEmpty(value) ? "—" : value;

    protected override SessionViewState Compose() => new(
        StatusVisible: _statusVisible,
        StatusHeadline: _statusHeadline,
        StatusDetail: _statusDetail,
        StatusBusy: _statusBusy,
        StatusActionsVisible: _statusTerminal,
        IsStreamLive: _isStreamLive,

        // Suppressed while the status overlay is up: that overlay is a stronger statement about the same
        // situation, and two notices about one problem read as two problems.
        AlertVisible: _alertRaised && !_statusVisible,
        Rung: _rung,
        Phase: _phase,

        // All of them, one per line: with several pads merged into one virtual controller, which devices are
        // contributing is exactly the thing that is otherwise invisible.
        ConnectedControllers: _controllers.Count switch
        {
            0 => Strings.Session_NoControllers,
            1 => _controllers.Values.First(),
            _ => string.Join("\n", _controllers.Values.Order()),
        },

        Diagnostics: _diagnostics);
}
