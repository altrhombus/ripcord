using System.Net;
using System.Net.Sockets;
using Ripcord.Core.Net.Stun;

namespace Ripcord.Cloud.Halyard.Rendezvous;

/// <summary>
/// Gathers this host's reflexive (public) endpoint on a given socket, for the reflexive candidate in the
/// rendezvous OFFER. A seam over <see cref="StunClient"/> so the rendezvous can be tested with a canned
/// address — and so a build with no outbound STUN can supply a null gatherer and simply offer LAN-only.
/// </summary>
public interface IReflexiveGatherer
{
    /// <summary>The public endpoint the socket maps to, or null when no STUN server answered.</summary>
    Task<IPEndPoint?> GatherAsync(UdpClient socket, CancellationToken cancellationToken);
}

/// <summary>The real gatherer: a STUN binding against public servers, on the media socket itself.</summary>
public sealed class StunReflexiveGatherer(StunClient? client = null, IReadOnlyList<DnsEndPoint>? servers = null)
    : IReflexiveGatherer
{
    private readonly StunClient _client = client ?? new StunClient();
    private readonly IReadOnlyList<DnsEndPoint> _servers = servers ?? StunClient.DefaultServers;

    public async Task<IPEndPoint?> GatherAsync(UdpClient socket, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPEndPoint> resolved =
            await StunClient.ResolveAsync(_servers, cancellationToken).ConfigureAwait(false);
        if (resolved.Count == 0)
        {
            return null;
        }

        StunResult result = await _client
            .GatherAsync(socket, resolved, TimeSpan.FromMilliseconds(500), cancellationToken)
            .ConfigureAwait(false);
        return result.ReflexiveEndpoint;
    }
}
