namespace Ripcord.Presentation.Sessions;

using Ripcord.Core.Consoles;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Resources;

/// <summary>One step of the connect sequence, as the user is told about it.</summary>
/// <param name="Headline">What is happening now, in the player's words.</param>
/// <param name="Detail">Why, or what it is waiting on. Rendered verbatim.</param>
/// <param name="Terminal">The sequence has stopped here and will not continue without the user.</param>
public sealed record ConnectStage(string Headline, string Detail, bool Terminal);

/// <summary>
/// Everything a caller needs to open the session, once the flow has decided it can be opened.
/// </summary>
/// <param name="Config">The session config, already corrected for the console's generation.</param>
/// <param name="Route">Local or through the account, with the reason already reported as a stage.</param>
public sealed record ConnectPlan(SessionConfig Config, StreamingRoute Route);

/// <summary>
/// The connect sequence: guard, configure, bring up video, check the client can speak the protocol at all,
/// wake the console, choose a route. Returns a <see cref="ConnectPlan"/> when a session can be opened, and
/// <see langword="null"/> when it cannot — in which case the last stage reported was terminal and says why.
///
/// <para>
/// <b>Why this is not in the page.</b> It lived in <c>SessionPage.xaml.cs</c> with its stage strings inline
/// in English, which made the longest-running, most-watched surface in the app the one piece of the session
/// lifecycle that could not be tested off-device or translated. Nothing here touches a GPU or a native
/// handle: the one part that does is behind <see cref="IVideoPipelinePreparer"/>.
/// </para>
///
/// <para>
/// <b>Each phase announces itself BEFORE it runs</b>, so when one hangs, the last message on screen names it.
/// Video device creation especially — it is a plausible place to stall on unfamiliar hardware, and it used to
/// be indistinguishable from a network problem because the overlay said "Connecting…" throughout.
/// </para>
///
/// <para>
/// <b>What this deliberately does not do:</b> build the session controller, own the power monitor, or start
/// the stream. Those need the pipeline, the input stack and a platform power source — devices, all of them —
/// so the caller does that with the plan this returns. The split is the same one the architecture rules draw:
/// a device gets a seam, data is passed as a value.
/// </para>
/// </summary>
public sealed class ConnectFlow
{
    private readonly IStreamingSessionSource _sessions;
    private readonly IConsoleWakeCoordinator _wake;
    private readonly IVideoPipelinePreparer _video;

    public ConnectFlow(
        IStreamingSessionSource sessions,
        IConsoleWakeCoordinator wake,
        IVideoPipelinePreparer video)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _wake = wake ?? throw new ArgumentNullException(nameof(wake));
        _video = video ?? throw new ArgumentNullException(nameof(video));
    }

    /// <summary>
    /// Run the sequence. <paramref name="stages"/> is reported on the calling thread; a UI caller marshals,
    /// which for this layer means handing it something that ends in <c>ObservableState.Mutate</c>.
    /// </summary>
    public async Task<ConnectPlan?> RunAsync(
        PairedConsole? console,
        RipcordSettings settings,
        IProgress<ConnectStage> stages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(stages);

        if (console is null)
        {
            Report(stages, Strings.Connect_NoConsoleHeadline, Strings.Connect_NoConsoleDetail, terminal: true);
            return null;
        }

        SessionConfig config = ConfigureFor(console, settings);

        Report(stages, Strings.Connect_PreparingVideoHeadline, Strings.Connect_PreparingVideoDetail, terminal: false);
        try
        {
            await _video.PrepareAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null; // the user left before the device came up
        }
        catch (Exception ex)
        {
            // The message is the diagnosis here — see IVideoPipelinePreparer on why this one throws.
            Report(stages, Strings.Connect_VideoFailedHeadline, ex.Message, terminal: true);
            return null;
        }

        Report(stages, Strings.Connect_CheckingCredentialsHeadline, Strings.Connect_CheckingCredentialsDetail, terminal: false);

        // FATAL, and it must say so. Without the control secrets the session crypto is a passthrough stub, so
        // the handshake can never complete. This used to be noted in the diagnostics panel and the connect was
        // attempted anyway, leaving "Connecting…" on screen indefinitely with no stated cause.
        StreamingAvailability streaming = _sessions.Availability;
        if (!streaming.Available)
        {
            Report(stages, Strings.Connect_NoConstantsHeadline, streaming.Detail, terminal: true);
            return null;
        }

        if (!await EnsureAwakeAsync(console, stages, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // Which route, and why — said out loud before it is taken. The account route costs tens of seconds,
        // and silence for that long reads as a hang; "connecting through your account because this console
        // isn't on your network" is the difference between a slow connect that makes sense and a broken one.
        StreamingRouteChoice choice;
        try
        {
            choice = await _sessions.ChooseRouteAsync(console, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        Report(stages, Strings.Connect_ConnectingHeadline, choice.Reason, terminal: false);
        return new ConnectPlan(config, choice.Route);
    }

    /// <summary>
    /// PS4 Remote Play is H.264 / SDR only — HEVC and HDR are PS5 features. Requesting HEVC makes the console
    /// reject the launchSpec silently (no SESSION_REPLY, so no stream), and the decoder codec has to match the
    /// launchSpec anyway. Forced regardless of the user's setting, and forced HERE rather than in the settings
    /// page, because the same config feeds both the launchSpec and the decode pipeline and they must agree.
    /// </summary>
    private static SessionConfig ConfigureFor(PairedConsole console, RipcordSettings settings)
    {
        SessionConfig config = settings.ToSessionConfig();

        return string.Equals(console.Platform, "Ps4", StringComparison.OrdinalIgnoreCase)
            ? config with { CodecPreference = VideoCodec.H264, RequestedDynamicRange = DynamicRange.Sdr }
            : config;
    }

    /// <summary>
    /// Wake the console if it is in standby. Returns false only when a wake was sent and the console never
    /// came up — the one outcome that genuinely cannot lead to a stream, so the connect stops with a stated
    /// reason. Everything else proceeds: a console that does not answer discovery may still be reachable, and
    /// letting the connect surface that is more useful than refusing to try.
    /// </summary>
    private async Task<bool> EnsureAwakeAsync(
        PairedConsole console,
        IProgress<ConnectStage> stages,
        CancellationToken cancellationToken)
    {
        // The coordinator's progress lines are headlines in their own right ("Waking your PS5…"), so they
        // replace the headline rather than sitting under one.
        //
        // NOT Progress<T>: that POSTS to the captured SynchronizationContext instead of invoking, so a wake
        // line could arrive after the stage that follows it and leave "Waking the console" on screen over a
        // session already connecting. Everything this flow reports is reported synchronously, in order, and
        // whoever supplied `stages` decides where it is marshalled to.
        var wakeProgress = new SynchronousProgress<string>(
            line => Report(stages, line, Strings.Connect_WakeDetail, terminal: false));

        ConsoleWakeOutcome outcome;
        try
        {
            outcome = await _wake.EnsureAwakeAsync(console, wakeProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false; // the user left mid-wake
        }

        if (outcome == ConsoleWakeOutcome.TimedOut)
        {
            Report(stages, Strings.Connect_DidNotWakeHeadline, Strings.Connect_DidNotWakeDetail, terminal: true);
            return false;
        }

        return true;
    }

    private static void Report(IProgress<ConnectStage> stages, string headline, string detail, bool terminal)
        => stages.Report(new ConnectStage(headline, detail, terminal));

    /// <summary>
    /// An <see cref="IProgress{T}"/> that calls its handler rather than posting it.
    ///
    /// <para>
    /// <see cref="Progress{T}"/> exists to marshal, which is the wrong job here twice over: this flow already
    /// reports every other stage synchronously, so mixing the two lets a wake line overtake the stage after
    /// it; and the caller has its own dispatcher and has already decided where these land.
    /// </para>
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
