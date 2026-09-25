using Ripcord.Client;
using Ripcord.Core.Reactive;
using Ripcord.Core.Input;
using Ripcord.Core.Sessions;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The session lifecycle policy: connect, degrade, reconnect with backoff, and give up usefully. All timing is
/// injected, so these run in milliseconds and assert the policy rather than racing a real clock — which is the
/// point of extracting this out of the XAML code-behind, where none of it could be tested at all.
/// </summary>
public class SessionControllerTests
{
    // ---- fakes ----

    private sealed class FakeSession : IStreamingSession
    {
        private readonly Subject<EncodedVideoFrame> _video = new();
        private readonly Subject<EncodedAudioFrame> _audio = new();
        private readonly Subject<SessionStatistics> _stats = new();

        public FakeSession(bool succeeds = true, string? failureReason = null)
        {
            Succeeds = succeeds;
            FailureReason = failureReason;
        }

        public bool Succeeds { get; }
        public string? FailureReason { get; }
        public bool Disposed { get; private set; }
        public int ConnectCalls { get; private set; }

        public SessionState State { get; set; } = SessionState.Connecting;

        /// <summary>
        /// Null by default = "the console has said nothing", so existing stall tests keep exercising the stall
        /// path. Tests for the idle-but-healthy case set this to a small value.
        /// </summary>
        public double? MillisecondsSinceConsoleActivity { get; set; }
        public IObservable<EncodedVideoFrame> VideoFrames => _video;
        public IObservable<EncodedAudioFrame> AudioFrames => _audio;
        public IObservable<SessionStatistics> Statistics => _stats;
        public bool RestConsoleOnDisconnect { get; set; }
        public ISessionInputSink InputSink { get; } = new CountingSink();

        /// <summary>How many controller frames the session has been handed.</summary>
        public int InputFrames => ((CountingSink)InputSink).Count;
        public IBandwidthController BandwidthController { get; } = new RecordingBandwidth();

        public Task<SessionHandshakeResult> ConnectAsync(SessionConfig config, CancellationToken cancellationToken)
        {
            ConnectCalls++;
            if (!Succeeds)
            {
                return Task.FromResult(new SessionHandshakeResult(false, FailureReason));
            }

            State = SessionState.Streaming;
            return Task.FromResult(new SessionHandshakeResult(true, null));
        }

        public void EmitFrame() => _video.OnNext(new EncodedVideoFrame(new byte[] { 1, 2, 3 }, 0, false));

        public int KeyFrameRequests { get; private set; }

        public void RequestKeyFrame() => KeyFrameRequests++;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            State = SessionState.Closed;
            return ValueTask.CompletedTask;
        }

        private sealed class CountingSink : ISessionInputSink
        {
            public int Count { get; private set; }
            public void SubmitControllerState(ControllerStateFrame frame) => Count++;
        }

        private sealed class RecordingBandwidth : IBandwidthController
        {
            public string LastDecisionReason => "test fake";
            public List<PowerState> Reported { get; } = [];
            public void ReportNetworkSample(NetworkSample sample) { }
            public void ReportThermalPowerState(PowerState state) => Reported.Add(state);
            public BitrateDecision RecommendBitrate() => new(10_000, 1280, 720, 60);
        }
    }

    private sealed class FakePipeline : IVideoDecodePipeline
    {
        public int VideoFrames { get; private set; }
        public UpscaleMode UpscaleMode { get; set; }
        public event Action? KeyFrameRequested;
        public Task StartAsync(SessionConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SubmitEncodedVideo(EncodedVideoFrame frame) => VideoFrames++;
        public void SubmitEncodedAudio(EncodedAudioFrame frame) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>Simulate the decoder deciding it cannot catch up.</summary>
        public void RaiseKeyFrameRequested() => KeyFrameRequested?.Invoke();
    }

    /// <summary>
    /// Decouples "how long did the code ask to wait" from "what time is it".
    ///
    /// <para>
    /// Delays record their requested duration and then yield briefly for real, so the controller's loops make
    /// progress without pegging a core. Crucially they do NOT advance the clock — only <see cref="Advance"/>
    /// does. An earlier version advanced time by the requested duration, which let the 1 ms watchdog loop race
    /// virtual time forward by minutes in a fraction of a real second and blow straight past thresholds the
    /// test was trying to sit between. Explicit advancement makes every threshold crossing deliberate.
    /// </para>
    /// </summary>
    private sealed class VirtualTime
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        private readonly List<TimeSpan> _delays = [];

        public DateTimeOffset Now()
        {
            lock (_gate) return _now;
        }

        /// <summary>Durations the code under test asked to wait, in order.</summary>
        public List<TimeSpan> RecordedDelays()
        {
            lock (_gate) return [.. _delays];
        }

        public async Task Delay(TimeSpan duration, CancellationToken ct)
        {
            lock (_gate)
            {
                _delays.Add(duration);
            }

            // A real, tiny yield: keeps the loop cooperative without tying the test to wall-clock timing.
            await Task.Delay(1, ct).ConfigureAwait(false);
        }

        public void Advance(TimeSpan by)
        {
            lock (_gate) _now += by;
        }
    }

    private static readonly SessionConfig Config =
        new(1280, 720, 60, 10_000, VideoCodec.H264, LatencyMode.Balanced);

    /// <summary>Spin the controller's background loop until <paramref name="condition"/> holds, or fail.</summary>
    private static async Task WaitFor(Func<bool> condition, string because)
    {
        for (int i = 0; i < 2_000 && !condition(); i++)
        {
            await Task.Delay(1);
        }

        Assert.True(condition(), because);
    }

    // ---- tests ----

    // ---- the per-attempt deadline ----------------------------------------------------------------
    //
    // MaxReconnectAttempts bounds how many attempts there are and says nothing about one that never returns.
    // The deadline is the backstop for that, and it is armed on the token the attempt is given - so the thing
    // most worth pinning is that a SUCCESSFUL attempt is disarmed, because the session keeps that token for
    // its whole lifetime and a live stream must not be cancelled out from under itself.
    //
    // Real time rather than VirtualTime, deliberately: CancelAfter runs on a real timer, and a test that
    // faked the clock would pass whatever the code did.

    [Fact]
    public async Task ASuccessfulConnect_IsNotCancelledWhenTheDeadlineWouldHaveElapsed()
    {
        var time = new VirtualTime();
        var session = new FakeSession();
        var pipeline = new FakePipeline();
        CancellationToken opened = default;

        await using var controller = new SessionController(
            token =>
            {
                opened = token;
                return Task.FromResult<IStreamingSession>(session);
            },
            pipeline,
            options: new SessionControllerOptions { ConnectTimeout = TimeSpan.FromMilliseconds(150) },
            clock: time.Now,
            delay: time.Delay);

        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should connect");

        // Well past the deadline. Without the disarm this token is cancelled by now and the stream with it.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.False(
            opened.IsCancellationRequested,
            "the session's token was cancelled by the connect deadline after the connect had already "
            + "succeeded, which would tear down a working stream");
        Assert.Equal(SessionLifecycle.Streaming, controller.Lifecycle);
    }

    [Fact]
    public async Task AnAttemptThatNeverReturns_FailsRatherThanHanging()
    {
        var time = new VirtualTime();
        var pipeline = new FakePipeline();

        await using var controller = new SessionController(
            async token =>
            {
                // Never answers, but honours cancellation — which is what lets the deadline release whatever
                // the attempt was holding instead of abandoning it still joined to a cloud session.
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
            pipeline,
            options: new SessionControllerOptions
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(150),
                MaxReconnectAttempts = 1,
                InitialBackoff = TimeSpan.Zero,
            },
            clock: time.Now,
            delay: time.Delay);

        await controller.StartAsync(Config);

        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Failed, "should give up rather than hang");
        Assert.Contains("didn't answer within", controller.CurrentStatus.Detail);
    }

    [Fact]
    public async Task StartsIdle_ThenReachesStreamingOnASuccessfulConnect()
    {
        var time = new VirtualTime();
        var session = new FakeSession();
        var pipeline = new FakePipeline();

        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(session), pipeline, clock: time.Now, delay: time.Delay);

        Assert.Equal(SessionLifecycle.Idle, controller.Lifecycle);

        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should reach Streaming");
    }

    [Fact]
    public async Task FramesAreForwardedToThePipeline()
    {
        var time = new VirtualTime();
        var session = new FakeSession();
        var pipeline = new FakePipeline();

        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(session), pipeline, clock: time.Now, delay: time.Delay);
        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should connect");

        session.EmitFrame();
        session.EmitFrame();

        Assert.Equal(2, pipeline.VideoFrames);
    }

    [Fact]
    public async Task RestConsoleOnDisconnect_SetAtDisconnect_ReachesTheLiveSession()
    {
        // The rest choice is made at the disconnect prompt, not at connect, so setting it on the controller must
        // land on the session that DisposeAsync reads — otherwise the checkbox would silently do nothing.
        var time = new VirtualTime();
        var session = new FakeSession();

        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(session), new FakePipeline(), clock: time.Now, delay: time.Delay);
        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should connect");

        // Config default is off, and the getter should reflect the live session.
        Assert.False(controller.RestConsoleOnDisconnect);
        Assert.False(session.RestConsoleOnDisconnect);

        controller.RestConsoleOnDisconnect = true;

        Assert.True(session.RestConsoleOnDisconnect);
        Assert.True(controller.RestConsoleOnDisconnect);
    }

    [Fact]
    public async Task SuspendInputForwarding_DropsFramesAndNeutralisesTheConsole()
    {
        // While an overlay (the disconnect prompt) owns the pad, frames must not leak into the game — and the
        // console must be released once, so a held gesture is not stuck down behind the dialog.
        var time = new VirtualTime();
        var session = new FakeSession();
        var input = new Subject<ControllerStateFrame>();

        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(session), new FakePipeline(), controllerInput: input, clock: time.Now, delay: time.Delay);
        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should connect");

        input.OnNext(default);
        Assert.Equal(1, session.InputFrames); // forwarded normally

        controller.SuspendInputForwarding = true;
        Assert.Equal(2, session.InputFrames); // the one neutral release frame

        input.OnNext(default);
        input.OnNext(default);
        Assert.Equal(2, session.InputFrames); // dropped while suspended

        controller.SuspendInputForwarding = false;
        input.OnNext(default);
        Assert.Equal(3, session.InputFrames); // forwarding resumes
    }

    [Fact]
    public async Task UnretryableFailure_FailsImmediatelyWithoutBurningRetries()
    {
        // "not paired" cannot be fixed by trying again; spending six backoffs on it wastes the user's time and
        // buries the actual problem.
        var time = new VirtualTime();
        var session = new FakeSession(succeeds: false, failureReason: "console is not paired");

        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(session), new FakePipeline(), clock: time.Now, delay: time.Delay);

        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Failed, "should fail fast");

        Assert.Equal(1, session.ConnectCalls);
        // No backoff waits: an unretryable failure must not sit in a retry loop.
        Assert.DoesNotContain(time.RecordedDelays(), d => d >= TimeSpan.FromSeconds(1));
        Assert.Contains("not paired", controller.CurrentStatus.Detail);
    }

    [Fact]
    public async Task RetryableFailure_RetriesWithExponentialBackoff_ThenGivesUp()
    {
        var time = new VirtualTime();
        int created = 0;
        var options = new SessionControllerOptions
        {
            MaxReconnectAttempts = 3,
            InitialBackoff = TimeSpan.FromSeconds(1),
            MaxBackoff = TimeSpan.FromSeconds(15),
        };

        await using var controller = new SessionController(
            _ =>
            {
                created++;
                return Task.FromResult<IStreamingSession>(
                    new FakeSession(succeeds: false, failureReason: "network unreachable"));
            },
            new FakePipeline(),
            options: options,
            clock: time.Now,
            delay: time.Delay);

        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Failed, "should exhaust retries");

        // Initial attempt + 3 retries.
        Assert.Equal(4, created);

        // Backoff doubles: 1s, 2s, 4s.
        var backoffs = time.RecordedDelays().Where(d => d >= TimeSpan.FromSeconds(1)).ToList();
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], backoffs);
        Assert.Contains("3 attempts", controller.CurrentStatus.Detail);
    }

    [Fact]
    public void BackoffIsCappedAtMaxBackoff()
    {
        var time = new VirtualTime();
        var options = new SessionControllerOptions
        {
            InitialBackoff = TimeSpan.FromSeconds(1),
            MaxBackoff = TimeSpan.FromSeconds(15),
        };

        var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(new FakeSession()), new FakePipeline(), options: options, clock: time.Now, delay: time.Delay);

        Assert.Equal(TimeSpan.FromSeconds(1), controller.BackoffFor(1));
        Assert.Equal(TimeSpan.FromSeconds(8), controller.BackoffFor(4));
        // Unbounded doubling would be ~9 hours by attempt 20; the cap keeps retries useful.
        Assert.Equal(TimeSpan.FromSeconds(15), controller.BackoffFor(20));
    }

    [Fact]
    public async Task AStaticSceneDoesNotDegradeWhileTheConsoleIsStillTalking()
    {
        // Observed in the wild: a Forza pause menu with no animation gives the encoder nothing to send, so frames
        // stop for tens of seconds while the console is entirely healthy. Escalating on frame silence alone showed
        // "Video has stopped — trying to recover…" over a working stream.
        var time = new VirtualTime();
        var session = new FakeSession { MillisecondsSinceConsoleActivity = 50 };
        var options = new SessionControllerOptions
        {
            StallTimeout = TimeSpan.FromSeconds(2),
            ReconnectAfterStall = TimeSpan.FromSeconds(6),
            WatchdogInterval = TimeSpan.FromMilliseconds(1),
        };

        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(session), new FakePipeline(), options: options, clock: time.Now, delay: time.Delay);

        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should connect");
        session.EmitFrame();

        // Well past BOTH thresholds with no frames at all — but the console keeps answering.
        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);

        Assert.Equal(SessionLifecycle.Streaming, controller.Lifecycle);
        Assert.False(session.Disposed);
        Assert.Equal(1, session.ConnectCalls);
    }

    [Fact]
    public async Task AStaticSceneThatThenGoesSilentIsStillTreatedAsAStall()
    {
        // The tolerance must not disable stall detection: once the console stops talking too, this is a real fault.
        var time = new VirtualTime();
        var session = new FakeSession { MillisecondsSinceConsoleActivity = 50 };
        var options = new SessionControllerOptions
        {
            StallTimeout = TimeSpan.FromSeconds(2),
            ReconnectAfterStall = TimeSpan.FromSeconds(60), // far away, so this exercises degrade only
            WatchdogInterval = TimeSpan.FromMilliseconds(1),
        };

        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(session), new FakePipeline(), options: options, clock: time.Now, delay: time.Delay);

        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should connect");
        session.EmitFrame();

        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(SessionLifecycle.Streaming, controller.Lifecycle); // idle, tolerated

        // Now the console goes quiet as well.
        session.MillisecondsSinceConsoleActivity = 10_000;
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Degraded, "console silence should degrade it");
    }

    [Fact]
    public async Task AnIdleStreamRecoversTheLifecycleAfterAnEarlierDip()
    {
        // A real dip degrades, then the console resumes talking but the scene is static so no frames arrive. The
        // status must return to Streaming rather than sticking on "Video has stopped".
        var time = new VirtualTime();
        var session = new FakeSession();   // null activity = console silent
        var options = new SessionControllerOptions
        {
            StallTimeout = TimeSpan.FromSeconds(2),
            ReconnectAfterStall = TimeSpan.FromSeconds(60),
            WatchdogInterval = TimeSpan.FromMilliseconds(1),
        };

        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(session), new FakePipeline(), options: options, clock: time.Now, delay: time.Delay);

        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should connect");
        session.EmitFrame();

        time.Advance(TimeSpan.FromSeconds(3));
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Degraded, "silence should degrade it");

        // Console starts answering again; still no frames, because nothing on screen is moving.
        session.MillisecondsSinceConsoleActivity = 20;
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "activity alone should recover it");
        Assert.Equal(1, session.ConnectCalls);
    }

    [Fact]
    public async Task StalledVideo_GoesDegraded_ThenRecoversWithoutReconnecting()
    {
        // The common case: a brief dip. Reconnecting drops the session on the console, so riding it out is
        // strictly better when frames come back.
        var time = new VirtualTime();
        var session = new FakeSession();
        var options = new SessionControllerOptions
        {
            StallTimeout = TimeSpan.FromSeconds(2),
            ReconnectAfterStall = TimeSpan.FromSeconds(60), // far away, so this test only exercises degrade
            WatchdogInterval = TimeSpan.FromMilliseconds(1),
        };

        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(session), new FakePipeline(), options: options, clock: time.Now, delay: time.Delay);

        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should connect");
        session.EmitFrame();

        // Push past the stall timeout but nowhere near the reconnect threshold.
        time.Advance(TimeSpan.FromSeconds(3));
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Degraded, "silence should degrade it");
        Assert.Contains("stopped", controller.CurrentStatus.Detail, StringComparison.OrdinalIgnoreCase);

        // Frames resume: back to Streaming, and the session was never replaced.
        session.EmitFrame();
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "frames should recover it");
        Assert.False(session.Disposed);
        Assert.Equal(1, session.ConnectCalls);
    }

    [Fact]
    public async Task StallPastTheReconnectThreshold_ReplacesTheSession()
    {
        var time = new VirtualTime();
        var sessions = new List<FakeSession>();
        var options = new SessionControllerOptions
        {
            StallTimeout = TimeSpan.FromSeconds(2),
            ReconnectAfterStall = TimeSpan.FromSeconds(6),
            WatchdogInterval = TimeSpan.FromMilliseconds(1),
            MaxReconnectAttempts = 3,
        };

        await using var controller = new SessionController(
            _ =>
            {
                var s = new FakeSession();
                sessions.Add(s);
                return Task.FromResult<IStreamingSession>(s);
            },
            new FakePipeline(),
            options: options,
            clock: time.Now,
            delay: time.Delay);

        await controller.StartAsync(Config);
        await WaitFor(() => sessions.Count >= 1, "should connect once");

        // No frames ever arrive, so the stall is measured from the handshake — a session that connects but
        // never delivers video must still be caught.
        time.Advance(TimeSpan.FromSeconds(7));
        await WaitFor(() => sessions.Count >= 2, "a dead session should be replaced");
        Assert.True(sessions[0].Disposed, "the stalled session should be disposed");
    }

    [Fact]
    public async Task ASessionThatLastsResetsTheRetryBudget()
    {
        // The original intent, which still holds: a long session with occasional blips must not slowly exhaust
        // its retries and then refuse to reconnect for a reason that has nothing to do with the current
        // problem.
        //
        // What changed is the test for "successful". This assertion used to be made about a session that
        // merely CONNECTED, and that is what made the reconnect loop unbounded on hardware — see
        // AConsoleThatConnectsAndDropsImmediately_EventuallyGivesUp below. Duration is the honest measure.
        var time = new VirtualTime();
        var sessions = new List<FakeSession>();
        var options = new SessionControllerOptions
        {
            StallTimeout = TimeSpan.FromSeconds(2),
            ReconnectAfterStall = TimeSpan.FromSeconds(6),
            WatchdogInterval = TimeSpan.FromMilliseconds(1),
            MaxReconnectAttempts = 2,
            MinimumHealthySession = TimeSpan.FromSeconds(5),
        };

        await using var controller = new SessionController(
            _ =>
            {
                var s = new FakeSession();
                sessions.Add(s);
                return Task.FromResult<IStreamingSession>(s);
            },
            new FakePipeline(),
            options: options,
            clock: time.Now,
            delay: time.Delay);

        await controller.StartAsync(Config);

        // Each session lives ~7s before the stall watchdog replaces it — comfortably past the 5s that counts
        // as a real session, so every replacement restores a full budget and this continues well past
        // MaxReconnectAttempts (2) rather than reaching Failed.
        for (int i = 0; i < 20 && sessions.Count < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(7));
            await Task.Delay(20);
        }

        Assert.True(sessions.Count >= 5, $"expected repeated reconnects, got {sessions.Count}");
        Assert.NotEqual(SessionLifecycle.Failed, controller.Lifecycle);
    }

    [Fact]
    public async Task AConsoleThatConnectsAndDropsImmediately_EventuallyGivesUp()
    {
        // The hardware failure this exists for: a console on its way into rest mode completes the handshake
        // and drops it straight away. Every attempt looked like a success, so the retry budget reset every
        // time and the client reconnected forever — the user's only way out was killing the app.
        //
        // Identical setup to the test above except that MinimumHealthySession is longer than the sessions
        // actually last, which is precisely the distinction the fix turns on.
        var time = new VirtualTime();
        var sessions = new List<FakeSession>();
        var options = new SessionControllerOptions
        {
            StallTimeout = TimeSpan.FromSeconds(2),
            ReconnectAfterStall = TimeSpan.FromSeconds(6),
            WatchdogInterval = TimeSpan.FromMilliseconds(1),
            MaxReconnectAttempts = 2,
            MinimumHealthySession = TimeSpan.FromSeconds(30),
            InitialBackoff = TimeSpan.FromSeconds(1),
        };

        await using var controller = new SessionController(
            _ =>
            {
                var s = new FakeSession();
                sessions.Add(s);
                return Task.FromResult<IStreamingSession>(s);
            },
            new FakePipeline(),
            options: options,
            clock: time.Now,
            delay: time.Delay);

        await controller.StartAsync(Config);

        for (int i = 0; i < 40 && controller.Lifecycle != SessionLifecycle.Failed; i++)
        {
            time.Advance(TimeSpan.FromSeconds(7));
            await Task.Delay(20);
        }

        Assert.Equal(SessionLifecycle.Failed, controller.Lifecycle);

        // Bounded, and the message names the likely cause rather than a generic timeout.
        Assert.True(sessions.Count <= options.MaxReconnectAttempts + 2, $"unbounded retries: {sessions.Count}");
        Assert.Contains("rest mode", controller.CurrentStatus.Detail);
    }

    [Fact]
    public async Task StopAsync_ClosesAndDisposesTheSession()
    {
        var time = new VirtualTime();
        var session = new FakeSession();

        var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(session), new FakePipeline(), clock: time.Now, delay: time.Delay);

        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should connect");

        await controller.StopAsync();

        Assert.Equal(SessionLifecycle.Closed, controller.Lifecycle);
        Assert.True(session.Disposed);
        Assert.True(controller.CurrentStatus.IsTerminal);
    }

    [Fact]
    public async Task StatusObservers_SeeTheTransitionSequence()
    {
        var time = new VirtualTime();

        // Status is published from the controller's background loop, so a consumer must be thread-safe — the
        // app layer marshals to its dispatcher. Reading an unsynchronised List here threw
        // "collection was modified", which is exactly the mistake a UI would make.
        var gate = new Lock();
        var seen = new List<SessionLifecycle>();
        List<SessionLifecycle> Snapshot() { lock (gate) return [.. seen]; }

        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(new FakeSession()), new FakePipeline(), clock: time.Now, delay: time.Delay);

        controller.Status.Subscribe(new Recorder(s => { lock (gate) seen.Add(s.Lifecycle); }));
        await controller.StartAsync(Config);
        await WaitFor(() => Snapshot().Contains(SessionLifecycle.Streaming), "should publish Streaming");

        List<SessionLifecycle> final = Snapshot();
        Assert.Equal(SessionLifecycle.Connecting, final[0]);
        Assert.Contains(SessionLifecycle.Streaming, final);
    }

    [Fact]
    public async Task DecoderResyncRequest_IsRoutedToTheSession()
    {
        // The recovery loop: the decoder decides it cannot catch up, the session asks the console for an IDR.
        // Neither knows about the other, so the controller has to be the one that connects them.
        var time = new VirtualTime();
        var session = new FakeSession();
        var pipeline = new FakePipeline();

        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(session), pipeline, clock: time.Now, delay: time.Delay);

        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should connect");

        pipeline.RaiseKeyFrameRequested();
        Assert.Equal(1, session.KeyFrameRequests);
    }

    [Fact]
    public async Task DecoderResyncRequest_AfterTeardown_IsHarmless()
    {
        // The request arrives on the decode thread and can race a teardown; it must not throw into that thread.
        var time = new VirtualTime();
        var pipeline = new FakePipeline();

        var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(new FakeSession()), pipeline, clock: time.Now, delay: time.Delay);

        await controller.StartAsync(Config);
        await WaitFor(() => controller.Lifecycle == SessionLifecycle.Streaming, "should connect");
        await controller.StopAsync();

        pipeline.RaiseKeyFrameRequested(); // no session to route to
    }

    [Fact]
    public async Task StartingTwice_IsRejected()
    {
        var time = new VirtualTime();
        await using var controller = new SessionController(
            _ => Task.FromResult<IStreamingSession>(new FakeSession()), new FakePipeline(), clock: time.Now, delay: time.Delay);

        await controller.StartAsync(Config);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(Config));
    }

    private sealed class Recorder(Action<SessionStatus> onNext) : IObserver<SessionStatus>
    {
        public void OnNext(SessionStatus value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
