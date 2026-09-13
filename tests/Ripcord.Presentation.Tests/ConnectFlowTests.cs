using Ripcord.Core.Consoles;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Sessions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The connect sequence, off-device.
///
/// <para>
/// None of this was testable before the sequence left <c>SessionPage.xaml.cs</c>: it needed a window, a GPU
/// and a console. The cases that matter are the refusals — every one of them used to be a path that could
/// only be exercised by unplugging something.
/// </para>
/// </summary>
public class ConnectFlowTests
{
    private static PairedConsole Ps5() => new("id", "Living Room PS5", "10.0.0.5", "Ps5", "blob");

    private static PairedConsole Ps4() => new("id4", "Bedroom PS4", "10.0.0.6", "Ps4", "blob");

    private sealed class StubSessions(
        StreamingAvailability? availability = null,
        StreamingRouteChoice? choice = null,
        Exception? routeFault = null) : IStreamingSessionSource
    {
        public StreamingAvailability Availability { get; } = availability ?? new StreamingAvailability(true, "ready");

        public int RouteCalls { get; private set; }

        public Task<StreamingRouteChoice> ChooseRouteAsync(PairedConsole console, CancellationToken cancellationToken)
        {
            RouteCalls++;
            if (routeFault is not null)
            {
                throw routeFault;
            }

            return Task.FromResult(choice ?? new StreamingRouteChoice(StreamingRoute.Local, "on your network"));
        }

        public Task<IStreamingSession> OpenAsync(
            PairedConsole console,
            StreamingRoute route,
            Func<bool, CancellationToken, Task<string?>>? loginPin,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("ConnectFlow must not open the session; the caller does.");
    }

    private sealed class StubWake(
        ConsoleWakeOutcome outcome = ConsoleWakeOutcome.AlreadyAwake,
        string[]? progressLines = null,
        Exception? fault = null) : IConsoleWakeCoordinator
    {
        public int Calls { get; private set; }

        public Task<ConsoleWakeOutcome> EnsureAwakeAsync(
            PairedConsole console,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (fault is not null)
            {
                throw fault;
            }

            foreach (string line in progressLines ?? [])
            {
                progress?.Report(line);
            }

            return Task.FromResult(outcome);
        }
    }

    private sealed class StubVideo(Exception? fault = null) : IVideoPipelinePreparer
    {
        public int Calls { get; private set; }

        public SessionConfig? Received { get; private set; }

        public Task PrepareAsync(SessionConfig config, CancellationToken cancellationToken)
        {
            Calls++;
            Received = config;
            return fault is not null ? Task.FromException(fault) : Task.CompletedTask;
        }
    }

    /// <summary>Collects stages synchronously, so assertions do not race a posted callback.</summary>
    private sealed class Stages : IProgress<ConnectStage>
    {
        public List<ConnectStage> Reported { get; } = [];

        public ConnectStage? Last => Reported.Count == 0 ? null : Reported[^1];

        public void Report(ConnectStage value) => Reported.Add(value);
    }

    private static async Task<(ConnectPlan? Plan, Stages Stages)> RunAsync(
        IStreamingSessionSource sessions,
        IConsoleWakeCoordinator wake,
        IVideoPipelinePreparer video,
        PairedConsole? console,
        RipcordSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        var stages = new Stages();
        var flow = new ConnectFlow(sessions, wake, video);
        ConnectPlan? plan = await flow.RunAsync(console, settings ?? new RipcordSettings(), stages, cancellationToken);
        return (plan, stages);
    }

    [Fact]
    public async Task HappyPath_ReturnsAPlanAndAnnouncesEveryPhaseBeforeItRuns()
    {
        var video = new StubVideo();
        var wake = new StubWake();
        (ConnectPlan? plan, Stages stages) = await RunAsync(new StubSessions(), wake, video, Ps5());

        Assert.NotNull(plan);
        Assert.Equal(StreamingRoute.Local, plan!.Route);
        Assert.Equal(1, video.Calls);
        Assert.Equal(1, wake.Calls);

        // Video is announced before it is prepared, which is the whole reason a stalled device creation is
        // distinguishable from a network problem.
        Assert.False(stages.Reported[0].Terminal);
        Assert.Contains("video", stages.Reported[0].Headline, StringComparison.OrdinalIgnoreCase);

        // The route's reason is carried through verbatim as the final detail.
        Assert.Equal("on your network", stages.Last!.Detail);
        Assert.DoesNotContain(stages.Reported, stage => stage.Terminal);
    }

    [Fact]
    public async Task NoConsole_StopsTerminallyAndTouchesNothing()
    {
        var video = new StubVideo();
        var wake = new StubWake();
        var sessions = new StubSessions();

        (ConnectPlan? plan, Stages stages) = await RunAsync(sessions, wake, video, console: null);

        Assert.Null(plan);
        Assert.True(stages.Last!.Terminal);
        Assert.Equal(0, video.Calls);
        Assert.Equal(0, wake.Calls);
        Assert.Equal(0, sessions.RouteCalls);
    }

    [Fact]
    public async Task VideoFailure_IsTerminalAndShowsTheExceptionMessage()
    {
        var video = new StubVideo(new InvalidOperationException("No compatible decoder on this adapter."));
        var wake = new StubWake();
        var sessions = new StubSessions();

        (ConnectPlan? plan, Stages stages) = await RunAsync(sessions, wake, video, Ps5());

        Assert.Null(plan);
        Assert.True(stages.Last!.Terminal);
        Assert.Equal("No compatible decoder on this adapter.", stages.Last.Detail);

        // Nothing past video runs: waking a console we cannot decode for is pure cost.
        Assert.Equal(0, wake.Calls);
        Assert.Equal(0, sessions.RouteCalls);
    }

    [Fact]
    public async Task MissingControlConstants_IsTerminalBeforeTheConsoleIsWoken()
    {
        var wake = new StubWake();
        var sessions = new StubSessions(new StreamingAvailability(false, "interop constants unavailable"));

        (ConnectPlan? plan, Stages stages) = await RunAsync(sessions, wake, new StubVideo(), Ps5());

        Assert.Null(plan);
        Assert.True(stages.Last!.Terminal);
        Assert.Equal("interop constants unavailable", stages.Last.Detail);

        // The point of the gate: without the constants the handshake can never complete, so waking the
        // console would spin it up for a session that cannot happen.
        Assert.Equal(0, wake.Calls);
        Assert.Equal(0, sessions.RouteCalls);
    }

    [Fact]
    public async Task WakeTimedOut_IsTheOnlyWakeOutcomeThatStopsTheConnect()
    {
        var sessions = new StubSessions();
        (ConnectPlan? plan, Stages stages) = await RunAsync(
            sessions, new StubWake(ConsoleWakeOutcome.TimedOut), new StubVideo(), Ps5());

        Assert.Null(plan);
        Assert.True(stages.Last!.Terminal);
        Assert.Equal(0, sessions.RouteCalls);
    }

    [Theory]
    [InlineData(ConsoleWakeOutcome.AlreadyAwake)]
    [InlineData(ConsoleWakeOutcome.Woken)]
    [InlineData(ConsoleWakeOutcome.Unknown)]
    [InlineData(ConsoleWakeOutcome.AskedRemotely)]
    public async Task EveryOtherWakeOutcome_Proceeds(ConsoleWakeOutcome outcome)
    {
        // A console that does not answer discovery may still be reachable, and letting the connect surface
        // that is more useful than refusing to try.
        (ConnectPlan? plan, Stages stages) = await RunAsync(
            new StubSessions(), new StubWake(outcome), new StubVideo(), Ps5());

        Assert.NotNull(plan);
        Assert.DoesNotContain(stages.Reported, stage => stage.Terminal);
    }

    [Fact]
    public async Task WakeProgressLines_BecomeHeadlinesRatherThanDetails()
    {
        (_, Stages stages) = await RunAsync(
            new StubSessions(), new StubWake(progressLines: ["Waking your PS5…"]), new StubVideo(), Ps5());

        // "Waking your PS5…" is the headline; it reads as a phase, not as a footnote under one.
        Assert.Contains(stages.Reported, stage => stage.Headline == "Waking your PS5…");
    }

    [Fact]
    public async Task Ps4_IsForcedToH264AndSdrWhateverTheSettingsSay()
    {
        var video = new StubVideo();
        var settings = new RipcordSettings { Codec = VideoCodec.Hevc, RequestHdr = true };

        (ConnectPlan? plan, _) = await RunAsync(new StubSessions(), new StubWake(), video, Ps4(), settings);

        // Requesting HEVC makes a PS4 reject the launchSpec silently — no SESSION_REPLY, so no stream.
        Assert.NotNull(plan);
        Assert.Equal(VideoCodec.H264, plan!.Config.CodecPreference);
        Assert.Equal(DynamicRange.Sdr, plan.Config.RequestedDynamicRange);

        // And the pipeline is built from the same corrected config, because the decoder codec has to match
        // the launchSpec. This is the assertion that would catch them drifting apart.
        Assert.Equal(VideoCodec.H264, video.Received!.CodecPreference);
        Assert.Equal(DynamicRange.Sdr, video.Received.RequestedDynamicRange);
    }

    [Fact]
    public async Task Ps5_KeepsWhateverTheSettingsAskedFor()
    {
        var settings = new RipcordSettings { Codec = VideoCodec.Hevc, RequestHdr = true };

        (ConnectPlan? plan, _) = await RunAsync(new StubSessions(), new StubWake(), new StubVideo(), Ps5(), settings);

        Assert.NotNull(plan);
        Assert.Equal(VideoCodec.Hevc, plan!.Config.CodecPreference);
    }

    [Fact]
    public async Task CancellationDuringWake_StopsQuietlyWithoutATerminalStage()
    {
        var sessions = new StubSessions();
        (ConnectPlan? plan, Stages stages) = await RunAsync(
            sessions,
            new StubWake(fault: new OperationCanceledException()),
            new StubVideo(),
            Ps5());

        // The user left the page. There is no one to read a failure message, and writing one would leave a
        // terminal overlay on a page that is being torn down.
        Assert.Null(plan);
        Assert.DoesNotContain(stages.Reported, stage => stage.Terminal);
        Assert.Equal(0, sessions.RouteCalls);
    }

    [Fact]
    public async Task CancellationDuringVideo_StopsQuietlyToo()
    {
        (ConnectPlan? plan, Stages stages) = await RunAsync(
            new StubSessions(),
            new StubWake(),
            new StubVideo(new OperationCanceledException()),
            Ps5());

        Assert.Null(plan);
        Assert.DoesNotContain(stages.Reported, stage => stage.Terminal);
    }

    [Fact]
    public async Task AccountRoute_ReportsItsReasonSoASlowConnectMakesSense()
    {
        var sessions = new StubSessions(
            choice: new StreamingRouteChoice(StreamingRoute.Account, "this console isn't on your network"));

        (ConnectPlan? plan, Stages stages) = await RunAsync(sessions, new StubWake(), new StubVideo(), Ps5());

        Assert.NotNull(plan);
        Assert.Equal(StreamingRoute.Account, plan!.Route);

        // The account route costs tens of seconds, and silence for that long reads as a hang.
        Assert.Equal("this console isn't on your network", stages.Last!.Detail);
    }

    [Fact]
    public void ConstructorRejectsAMissingSeamRatherThanFailingMidConnect()
    {
        Assert.Throws<ArgumentNullException>(() => new ConnectFlow(null!, new StubWake(), new StubVideo()));
        Assert.Throws<ArgumentNullException>(() => new ConnectFlow(new StubSessions(), null!, new StubVideo()));
        Assert.Throws<ArgumentNullException>(() => new ConnectFlow(new StubSessions(), new StubWake(), null!));
    }

    [Fact]
    public async Task StagesArriveInOrder_IncludingTheOnesRelayedFromTheWakeCoordinator()
    {
        // Progress<T> POSTS rather than invoking, so a wake line reported through one can land after the
        // stage that follows it - leaving "Waking the console" on screen over a session already connecting.
        // This passed by luck until an unrelated change shifted the timing, so it is pinned here.
        (_, Stages stages) = await RunAsync(
            new StubSessions(),
            new StubWake(progressLines: ["Waking your PS5…"]),
            new StubVideo(),
            Ps5());

        int wake = stages.Reported.FindIndex(stage => stage.Headline == "Waking your PS5…");
        Assert.True(wake >= 0, "the wake line never arrived at all");
        Assert.Equal(stages.Reported.Count - 2, wake);
    }
}
