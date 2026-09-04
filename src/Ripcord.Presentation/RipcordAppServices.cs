using Ripcord.Core.Consoles;
using Ripcord.Core.Platform;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Accounts;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Pairing;
using Ripcord.Presentation.Sessions;
using Ripcord.Presentation.Settings;
using Ripcord.Presentation.Threading;

namespace Ripcord.Presentation;

/// <summary>
/// The application's object graph, built once at startup.
///
/// <para>
/// Before this existed, each surface constructed what it needed: three separate <c>SettingsStore</c>s, three
/// separate <c>PairedConsoleStore</c>s, and a scattering of <c>DefaultPlatformPaths</c>. That is not merely
/// duplication — two stores over one file is a coherence question nobody had answered, and a page could not be
/// pointed at a substitute for testing because it named its collaborator directly.
/// </para>
///
/// <para>
/// <b>Hand-rolled, and no container.</b> The graph is a handful of singletons and a handful of factories,
/// eagerly built, with no scopes and no lifetimes. <c>Microsoft.Extensions.DependencyInjection</c> would buy
/// reflection activation — which works against this repo's committed source-gen-JSON/trimming direction, and is
/// the first thing that has to come out of a NativeAOT shared library for a future non-.NET front end — and,
/// more expensively, an <c>IServiceProvider</c> within reach, which becomes service location at the first
/// awkward call site. What is here instead is a typed record of seams: there is no <c>Resolve&lt;T&gt;()</c>,
/// so nothing can ask for something the graph does not declare.
/// </para>
///
/// <para>
/// <b>Why it is reachable statically from a front end.</b> A page navigated to by type must have a parameterless
/// constructor, so a surface has to be able to find the graph rather than be handed it. The property that keeps
/// this from being a service locator is that the graph is a fixed set of named, typed members — a surface takes
/// what it needs in its constructor and holds that, rather than keeping the graph around to query later.
/// <see cref="Shell"/> is the one thing read at its point of use instead, because it is attached after the graph
/// is built and so cannot be captured in a constructor that may run first.
/// </para>
/// </summary>
public sealed class RipcordAppServices
{
    private IShellNavigator? _shell;

    public required IUiDispatcher Dispatcher { get; init; }

    public required IPlatformPaths Paths { get; init; }

    public required ISettingsStore Settings { get; init; }

    public required IPairedConsoleStore Consoles { get; init; }

    public required IConsoleScanner Scanner { get; init; }

    public required IConsoleRegistrar Registrar { get; init; }

    /// <summary>
    /// Pairing through the signed-in account, with no code off the console's screen. Always present, for the
    /// same reason <see cref="Account"/> is: a build with no credential gets an
    /// <see cref="UnavailableAccountPairing"/> that says why, rather than a null the add-console flow has to
    /// branch on.
    /// </summary>
    public required IAccountConsolePairing AccountPairing { get; init; }

    public required IConsoleReachabilityProbe ReachabilityProbe { get; init; }

    public required IConsoleWakeCoordinator WakeCoordinator { get; init; }

    /// <summary>
    /// The account tier. Always present — a build with no OAuth credential gets a session that reports
    /// <see cref="IAccountSession.CanSignIn"/> false rather than a null here, so no surface has to null-check
    /// before asking, and "sign-in is unavailable" is a state the UI renders rather than a branch it takes.
    /// </summary>
    public required IAccountSession Account { get; init; }

    /// <summary>
    /// What this machine's graphics stack can do. In the graph rather than constructed per surface because two
    /// surfaces ask (settings and about), and one of them asking differently is exactly how the UI-thread crash
    /// happened — see <see cref="IVideoCapabilitiesProbe"/>.
    /// </summary>
    public required IVideoCapabilitiesProbe VideoCapabilities { get; init; }

    /// <summary>
    /// The shell, once the front end has one.
    ///
    /// <para>
    /// The one late-bound member, and unavoidably so: the graph is built before the window, because the window's
    /// own construction already needs the settings store. Attached rather than injected, and it throws instead of
    /// returning null — a shell that was never attached is a wiring mistake at startup, and this is the stack
    /// trace that says so. The alternative, a nullable that pages null-check, buys silence: the symptom becomes a
    /// Connect button that does nothing, which is what the <c>as MainWindow</c> casts already did.
    /// </para>
    /// </summary>
    public IShellNavigator Shell => _shell ?? throw new InvalidOperationException(
        "No shell has been attached to RipcordAppServices. The front end must call AttachShell(...) as it "
        + "creates its main window, before any surface that shows or closes a stream can run.");

    /// <summary>Attach the shell. Called once, by the front end, as its main window is constructed.</summary>
    public void AttachShell(IShellNavigator shell)
    {
        ArgumentNullException.ThrowIfNull(shell);

        if (_shell is not null && !ReferenceEquals(_shell, shell))
        {
            throw new InvalidOperationException(
                "A different shell is already attached. One graph serves one shell; a second window needs its "
                + "own services, not a reassignment of these.");
        }

        _shell = shell;
    }

    // ---- factories -------------------------------------------------------------------------------
    //
    // Everything below is per-surface rather than shared, which is the whole distinction this type draws: a
    // store is one object the app over, a view-model belongs to the surface showing it and dies with it.

    public AddConsoleFlow CreateAddConsoleFlow()
        => new(Scanner, Registrar, Consoles, Dispatcher, account: Account, accountPairing: AccountPairing);

    /// <summary>
    /// The account surface's view-model. Per-surface like the others, but note that the session it wraps is the
    /// shared one: two account surfaces would show the same sign-in state, which is the intent.
    /// </summary>
    public AccountViewModel CreateAccountViewModel()
        => new(Account, Dispatcher, Consoles);

    public ConsoleReachabilityMonitor CreateReachabilityMonitor()
        => new(ReachabilityProbe);

    public ConsoleCardViewModel CreateConsoleCard(PairedConsole console)
        => new(console, Dispatcher);

    /// <summary>
    /// Build the streaming surface's view-model over <paramref name="pipeline"/>. The pipeline comes from the
    /// caller rather than the graph because it is a device the surface itself creates and destroys — see
    /// <see cref="IVideoPipelineStats"/>.
    /// </summary>
    public SessionViewModel CreateSessionViewModel(IVideoPipelineStats pipeline)
        => new(pipeline, Settings.Current, Dispatcher);

    public SettingsViewModel CreateSettingsViewModel()
        => new(Settings, Consoles, VideoCapabilities, Dispatcher);
}
