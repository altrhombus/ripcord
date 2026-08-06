using System.Net;
using Ripcord.Core.Consoles;
using Ripcord.Presentation.Consoles;
using Ripcord.Protocol.Halyard.Common.Discovery;

namespace Ripcord.Presentation.Halyard.Consoles;

/// <summary>
/// Reachability by SRCH probe, on the console's own family port and protocol version.
///
/// <para>
/// The family matters and is easy to get wrong: a PS4 answers on UDP 987 with
/// <c>device-discovery-protocol-version:00020020</c> while a PS5 answers on 9302 with <c>00030010</c>, so a
/// shared client pointed at the wrong family reports a perfectly healthy console as offline. Resolving the
/// profile per console rather than once per page is what makes a mixed PS4/PS5 list work.
/// </para>
/// </summary>
public sealed class HalyardReachabilityProbe : IConsoleReachabilityProbe
{
    /// <summary>
    /// How long to wait for an answer. Short on purpose: this fills in a status dot on a list the user is
    /// already looking at, and a console that has not answered in a second is not going to look any more online
    /// in three.
    /// </summary>
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(1);

    public async Task<bool?> ProbeAsync(PairedConsole console, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);

        // A stored host that will not parse is not something to probe. Reported as no-reply rather than thrown:
        // the outcome the user should see is "offline", and pairing stores an IP so this is defensive only.
        if (!IPAddress.TryParse(console.Host, out IPAddress? address))
        {
            return null;
        }

        var search = new HalyardSearchClient(HalyardDiscoveryProfile.ForPlatformName(console.Platform));
        HalyardSearchResult? result = await search
            .ProbeAsync(address, ProbeWindow, cancellationToken)
            .ConfigureAwait(false);

        // null = no reply at all; otherwise 200 (awake) vs 620 (standby).
        return result?.IsAwake;
    }
}
