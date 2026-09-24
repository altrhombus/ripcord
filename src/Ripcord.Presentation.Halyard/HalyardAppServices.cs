using Ripcord.Protocol.Halyard.Discovery;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Session;
using Ripcord.Cloud.Halyard;
using Ripcord.Core.Accounts;
using Ripcord.Core.Consoles;
using Ripcord.Core.Platform;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Accounts;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Halyard.Accounts;
using Ripcord.Presentation.Halyard.Consoles;
using Ripcord.Presentation.Halyard.Pairing;
using Ripcord.Presentation.Halyard.Sessions;
using Ripcord.Presentation.Pairing;
using Ripcord.Presentation.Sessions;
using Ripcord.Presentation.Settings;
using Ripcord.Presentation.Threading;

namespace Ripcord.Presentation.Halyard;

/// <summary>
/// Builds the application graph with the PlayStation backend plugged into it.
///
/// <para>
/// <b>The one place a front end names Halyard.</b> That is the point of this type existing separately from
/// <see cref="RipcordAppServices"/>: a shell calls <see cref="Create"/> once at startup and thereafter deals
/// only in the portable seams. Adding a second console family means writing another <c>Create</c> here — or
/// widening this one — rather than editing every surface that scans, registers or probes.
/// </para>
/// </summary>
public static class HalyardAppServices
{
    /// <summary>
    /// Assemble the graph.
    ///
    /// <para>
    /// Every collaborator is an optional parameter defaulting to the real thing, which is this codebase's
    /// existing house style for a seam and is what lets a harness — or a future integration test that wants a
    /// real graph over a temporary directory — substitute one piece without restating the other seven.
    /// </para>
    /// </summary>
    /// <param name="dispatcher">
    /// The front end's marshalling seam. No default: this is the one thing only the front end can supply, and
    /// guessing it (a synchronization context that may not exist yet) is how a view-model ends up mutating from
    /// the wrong thread in exactly one host.
    /// </param>
    /// <param name="videoCapabilities">
    /// What the front end's graphics stack can do. Like the dispatcher, only the front end can answer this, and
    /// on Windows the implementation must marshal off the UI thread or the process dies — which is precisely why
    /// it is one object in the graph rather than something each surface builds for itself.
    /// </param>
    public static RipcordAppServices Create(
        IUiDispatcher dispatcher,
        IVideoCapabilitiesProbe videoCapabilities,
        IPlatformPaths? paths = null,
        ISettingsStore? settings = null,
        IPairedConsoleStore? consoles = null,
        IConsoleScanner? scanner = null,
        IConsoleRegistrar? registrar = null,
        IConsoleReachabilityProbe? reachabilityProbe = null,
        IConsoleWakeCoordinator? wakeCoordinator = null,
        IAccountSession? account = null,
        IAccountConsolePairing? accountPairing = null,
        IAccountTokenStore? accountTokens = null,
        IDeviceIdentity? deviceIdentity = null,
        IStreamingSessionSource? sessions = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(videoCapabilities);

        IPlatformPaths resolvedPaths = paths ?? new DefaultPlatformPaths();

        // One gateway serves the account tier and account pairing, built at most once and only if something
        // asks — a caller that substitutes both seams must not pay for a device-id lookup, or fail on a host
        // that cannot supply one. Hence a memoised local rather than a field or an eager call.
        HalyardAccountGateway? gateway = null;
        bool gatewayResolved = false;
        HalyardAccountGateway? Gateway()
        {
            if (!gatewayResolved)
            {
                gatewayResolved = true;
                gateway = TryBuildGateway(resolvedPaths, accountTokens, deviceIdentity);
            }

            return gateway;
        }

        IAccountSession resolvedAccount = account
            ?? (Gateway() is { } forSession ? new HalyardAccountSession(forSession) : new UnavailableAccountSession());

        IPairedConsoleStore resolvedConsoles = consoles ?? new PairedConsoleStore(resolvedPaths);

        return new RipcordAppServices
        {
            Account = resolvedAccount,

            // Built here rather than by the streaming surface, which is where it used to be: the page loaded
            // the control secrets and constructed the session factory itself, so the one front end that
            // streams named the PlayStation backend directly. Choosing between the local and account routes
            // needs the account tier as well, and a page deciding that for itself would be the second place in
            // the app with an opinion about what "signed in" means.
            Sessions = sessions ?? BuildSessionSource(resolvedConsoles, Gateway),

            // Absent rather than broken when there is no gateway: the code route is unaffected, and the flow
            // renders the reason instead of offering an action that cannot work.
            AccountPairing = accountPairing
                ?? (Gateway() is { } forPairing
                    // The trace sink is null unless RIPCORD_TRACE_PAIRING says otherwise. The rendezvous
                    // narrates its own stages and the app was throwing that away, which left an account
                    // pairing that failed saying only what it had been waiting for.
                    ? new HalyardAccountConsolePairing(
                        forPairing,
                        options: new HalyardAccountPairingOptions
                        {
                            Log = HalyardPairingTrace.SinkIfEnabled(resolvedPaths),
                        })
                    : new UnavailableAccountPairing()),
            Dispatcher = dispatcher,
            VideoCapabilities = videoCapabilities,
            Paths = resolvedPaths,

            // Both stores are handed the SAME paths instance, which was not true when each surface built its
            // own: three settings stores over one file each re-read it at a different moment and each held its
            // own idea of the current settings, so a change made on one page was invisible to another until the
            // app restarted.
            Settings = settings ?? new SettingsStore(resolvedPaths),
            Consoles = resolvedConsoles,

            Scanner = scanner ?? new HalyardConsoleScanner(),
            Registrar = registrar ?? new HalyardConsoleRegistrar(),
            ReachabilityProbe = reachabilityProbe ?? new HalyardReachabilityProbe(),
            // Local wake first, with the account service as a fallback for a console that is not on this
            // network. Wrapped here rather than inside the Halyard coordinator so the local path stays free of
            // any account dependency — a build with no credential behaves exactly as it did.
            WakeCoordinator = wakeCoordinator
                ?? new CloudFallbackWakeCoordinator(new HalyardConsoleWakeCoordinator(), resolvedAccount),
        };
    }

    /// <summary>
    /// The account service gateway, or null when this build or this machine cannot reach the account tier at
    /// all.
    ///
    /// <para>
    /// The decision is made once, here, rather than inside each seam: a gateway constructed without a
    /// credential would be an object whose every method declines, and the graph is a better place to say "this
    /// capability is absent" than each of its methods. It also means the machine's device id is only resolved
    /// when it will actually be used — <see cref="HalyardClientDeviceId"/> throws on a host that cannot supply
    /// one, and a build that will never sign in should not fail to start over it.
    /// </para>
    ///
    /// <para>
    /// Null feeds two null objects rather than one, because two capabilities ride on this: signing in, and
    /// pairing through the account. They are absent together, always — the second is a use of the first.
    /// </para>
    /// </summary>
    /// <summary>
    /// The session source: the local route always, the account route when this build has a gateway.
    ///
    /// <para>
    /// The account connector is built <b>per attempt</b> rather than once, because it holds a single-use push
    /// socket — a connect that has been tried and torn down cannot be tried again on the same object, and
    /// connecting is exactly the thing a user retries.
    /// </para>
    /// </summary>
    private static IStreamingSessionSource BuildSessionSource(
        IPairedConsoleStore consoles, Func<HalyardAccountGateway?> gateway)
    {
        var factory = new HalyardSessionFactory(
            HalyardControlSecretsLoader.Load(out string cryptoSource),
            new PairedConsoleCredentialStore(consoles));

        return new HalyardStreamingSessionSource(
            factory,
            progress => gateway() is { } live
                ? new HalyardAccountConsoleSession(
                    live,
                    factory,
                    options: new HalyardAccountPairingOptions
                    {
                        // The rendezvous takes tens of seconds and says useful things while it does; without
                        // this the surface shows one unchanging line and reads as a hang.
                        Log = progress is null ? null : progress.Report,
                    })
                : null,
            () => gateway() is not null,
            cryptoSource);
    }

    private static HalyardAccountGateway? TryBuildGateway(
        IPlatformPaths paths, IAccountTokenStore? tokens, IDeviceIdentity? deviceIdentity)
    {
        HalyardClientConfig config = HalyardClientConfigFile.Load(paths);
        if (!config.IsConfigured)
        {
            return null;
        }

        try
        {
            return new HalyardAccountGateway(
                new HttpClient(),
                config,
                tokens ?? new AccountTokenStore(paths),
                deviceIdentity ?? new DefaultDeviceIdentity());
        }
        catch (InvalidOperationException)
        {
            // The host could not supply a stable device id. The account tier is genuinely unavailable on this
            // machine, and reporting that through the same path as "no credential" means the UI already handles
            // it.
            return null;
        }
    }
}
