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
    /// Assemble the graph. Every collaborator is an optional parameter defaulting to the real thing, so a
    /// harness or an integration test can substitute one piece without restating the others.
    /// </summary>
    /// <param name="dispatcher">
    /// The front end's marshalling seam. No default: only the front end can supply it, and guessing it (a
    /// synchronization context that may not exist yet) would let a view-model mutate from the wrong thread.
    /// </param>
    /// <param name="videoCapabilities">
    /// What the front end's graphics stack can do. Only the front end can answer this, and on Windows the
    /// implementation must marshal off the UI thread or the process dies, which is why it is one object in
    /// the graph rather than something each surface builds for itself.
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

        // **One sink for the whole graph, not one per consumer.** With one each for pairing and the session
        // source, a trace would open with two headers and every line would have two clocks to be measured against.
        // Null unless RIPCORD_TRACE_PAIRING asks, and passed on as a plain Action so nothing below has to
        // know what a trace is.
        Action<string>? sharedTrace = HalyardPairingTrace.SinkIfEnabled(resolvedPaths);

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

            // Built here rather than by the streaming surface: a page that loaded the control secrets and
            // constructed the session factory itself would name the PlayStation backend directly. Choosing
            // between the local and account routes
            // needs the account tier as well, and a page deciding that for itself would be the second place in
            // the app with an opinion about what "signed in" means.
            Sessions = sessions ?? BuildSessionSource(resolvedConsoles, Gateway, sharedTrace),

            // Absent rather than broken when there is no gateway: the code route is unaffected, and the flow
            // renders the reason instead of offering an action that cannot work.
            AccountPairing = accountPairing
                ?? (Gateway() is { } forPairing
                    // The trace sink is null unless RIPCORD_TRACE_PAIRING says otherwise. The rendezvous
                    // narrates its own stages; without the sink, an account pairing that fails says only
                    // what it had been waiting for.
                    ? new HalyardAccountConsolePairing(
                        forPairing,
                        options: new HalyardAccountPairingOptions { Log = sharedTrace })
                    : new UnavailableAccountPairing()),
            // The connect sequence reports its stages here as well as to the surface. The surface shows the
            // latest line; a connect that sits in one stage for minutes needs the one that came before it.
            DiagnosticTrace = sharedTrace,

            Dispatcher = dispatcher,
            VideoCapabilities = videoCapabilities,
            Paths = resolvedPaths,

            // Both stores are handed the SAME paths instance, and each is built once. A settings store per
            // surface would be several stores over one file, each re-reading it at a different moment and each
            // holding its own idea of the current settings, so a change made on one page would be invisible to
            // another until the app restarted.
            Settings = settings ?? new SettingsStore(resolvedPaths),
            Consoles = resolvedConsoles,

            Scanner = scanner ?? new HalyardConsoleScanner(),
            Registrar = registrar ?? new HalyardConsoleRegistrar(),
            ReachabilityProbe = reachabilityProbe ?? new HalyardReachabilityProbe(),
            // The local wake only. A console that is not on this network is woken by the account route's own
            // connect command, which is the command the console has to answer anyway. A separate remote wake going
            // first creates a session of its own and sends the console a connect command for it; the console
            // joins that one, and the route's messages to it are refused as "Member not joined session." from
            // rest and even awake (traced from a phone hotspot, 2026-10-10).
            WakeCoordinator = wakeCoordinator ?? new HalyardConsoleWakeCoordinator(),
        };
    }

    /// <summary>
    /// The session source: the local route always, the account route when this build has a gateway.
    ///
    /// <para>
    /// The account connector is built <b>per attempt</b>: it holds a single-use push socket, so a connect
    /// that has been tried and torn down cannot be tried again on the same object.
    /// </para>
    /// </summary>
    private static IStreamingSessionSource BuildSessionSource(
        IPairedConsoleStore consoles, Func<HalyardAccountGateway?> gateway, Action<string>? trace)
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
                        Log = Tee(progress is null ? null : progress.Report, trace),
                    })
                : null,
            () => gateway() is not null,
            cryptoSource);
    }

    /// <summary>
    /// Send each line to both sinks, or to whichever one exists.
    ///
    /// <para>
    /// The surface and the trace want the same lines for different lifetimes: the surface shows the current
    /// one, the file keeps all of them. Returning null when neither exists matters — the options field reads
    /// null as "no log" and skips the formatting entirely.
    /// </para>
    /// </summary>
    private static Action<string>? Tee(Action<string>? first, Action<string>? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return line =>
        {
            first(line);
            second(line);
        };
    }

    /// <summary>
    /// The account service gateway, or null when this build or this machine cannot reach the account tier at
    /// all: the client configuration holds no OAuth client credential, or the host cannot supply a stable
    /// device id.
    ///
    /// <para>
    /// Decided once, here, rather than inside each seam: the graph is the place to say "this capability is
    /// absent". It also means the machine's device id is resolved only when it will be used —
    /// <see cref="HalyardClientDeviceId"/> throws on a host that cannot supply one, and a build that cannot
    /// sign in should not fail to start over it.
    /// </para>
    ///
    /// <para>
    /// Null feeds two null objects, because two capabilities ride on this and are absent together: signing
    /// in, and pairing through the account. The session source's account route is absent with them.
    /// </para>
    /// </summary>
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

                // **Not HttpClient's default hundred seconds.** Every call through here is a short REST
                // request the user is waiting on, and the connect path retries six times - so one
                // hundred-second hang is ten minutes of a window reading "Connecting" with a flash of
                // "Reconnecting" between attempts. Observed exactly that way on a phone hotspot, with an
                // empty trace, which looks like the code never ran. Twenty seconds is long enough for a
                // slow link to finish a token refresh and short enough to report a dead one while the
                // user is still watching.
                new HttpClient { Timeout = TimeSpan.FromSeconds(20) },
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
