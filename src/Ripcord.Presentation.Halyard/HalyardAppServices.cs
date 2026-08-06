using Ripcord.Core.Consoles;
using Ripcord.Core.Platform;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Halyard.Consoles;
using Ripcord.Presentation.Halyard.Pairing;
using Ripcord.Presentation.Pairing;
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
    public static RipcordAppServices Create(
        IUiDispatcher dispatcher,
        IPlatformPaths? paths = null,
        ISettingsStore? settings = null,
        IPairedConsoleStore? consoles = null,
        IConsoleScanner? scanner = null,
        IConsoleRegistrar? registrar = null,
        IConsoleReachabilityProbe? reachabilityProbe = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        IPlatformPaths resolvedPaths = paths ?? new DefaultPlatformPaths();

        return new RipcordAppServices
        {
            Dispatcher = dispatcher,
            Paths = resolvedPaths,

            // Both stores are handed the SAME paths instance, which was not true when each surface built its
            // own: three settings stores over one file each re-read it at a different moment and each held its
            // own idea of the current settings, so a change made on one page was invisible to another until the
            // app restarted.
            Settings = settings ?? new SettingsStore(resolvedPaths),
            Consoles = consoles ?? new PairedConsoleStore(resolvedPaths),

            Scanner = scanner ?? new HalyardConsoleScanner(),
            Registrar = registrar ?? new HalyardConsoleRegistrar(),
            ReachabilityProbe = reachabilityProbe ?? new HalyardReachabilityProbe(),
        };
    }
}
