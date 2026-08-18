using System.Net;
using System.Net.Sockets;

namespace Ripcord.Core.Net.Stun;

/// <summary>What a STUN binding attempt found.</summary>
/// <param name="ReflexiveEndpoint">
/// This host's public address as the server saw it — the reflexive candidate to advertise. Null when no
/// server answered or none returned a mapped address.
/// </param>
/// <param name="Server">The server that answered, for diagnostics.</param>
public sealed record StunResult(IPEndPoint? ReflexiveEndpoint, IPEndPoint? Server)
{
    public bool Succeeded => ReflexiveEndpoint is not null;
}

/// <summary>
/// A minimal STUN client: send a Binding Request, read back this host's reflexive address (RFC 5389).
///
/// <para>
/// The point of it is one value — the public <c>ip:port</c> a NAT assigns to us — which becomes the reflexive
/// candidate in a signaling OFFER. Ripcord can produce its LAN candidate on its own; it cannot see its own
/// reflexive address without asking something outside the NAT, and this is that ask. Off the LAN path it is
/// the difference between an OFFER a remote console can act on and one it cannot.
/// </para>
///
/// <para>
/// <b>The socket is the caller's to reuse.</b> A reflexive binding is only meaningful for the specific local
/// port it was gathered on — NAT maps per source port — so the candidate is worthless unless the media
/// transport later sends from that same socket. <see cref="GatherAsync(UdpClient, IReadOnlyList{IPEndPoint},
/// TimeSpan, CancellationToken)"/> takes the socket to bind on for exactly that reason; the convenience
/// overload that makes its own socket is for discovery and tests, where the mapping is not carried forward.
/// </para>
/// </summary>
public sealed class StunClient
{
    /// <summary>Public STUN servers, tried in order until one answers. Reflexive discovery is server-agnostic.</summary>
    public static IReadOnlyList<DnsEndPoint> DefaultServers { get; } =
    [
        new("stun.l.google.com", 19302),
        new("stun1.l.google.com", 19302),
        new("stun.cloudflare.com", 3478),
    ];

    private readonly TimeSpan _perServerTimeout;
    private readonly int _attemptsPerServer;

    public StunClient(TimeSpan? perServerTimeout = null, int attemptsPerServer = 3)
    {
        // Short, because a signaling exchange should not stall on a slow server when another will answer, and
        // retried a few times, because UDP to a public server drops the occasional datagram.
        _perServerTimeout = perServerTimeout ?? TimeSpan.FromMilliseconds(500);
        _attemptsPerServer = Math.Max(1, attemptsPerServer);
    }

    /// <summary>
    /// Discover the reflexive endpoint using a throwaway socket. For discovery and tests — the binding is not
    /// carried into a later media flow, so the port it was gathered on does not matter.
    /// </summary>
    public async Task<StunResult> DiscoverAsync(
        IReadOnlyList<IPEndPoint> servers, CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        return await GatherAsync(socket, servers, _perServerTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolve <see cref="DefaultServers"/> and discover against them.</summary>
    public async Task<StunResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IPEndPoint> servers = await ResolveAsync(DefaultServers, cancellationToken).ConfigureAwait(false);
        return servers.Count == 0
            ? new StunResult(null, null)
            : await DiscoverAsync(servers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gather the reflexive endpoint <b>on <paramref name="socket"/></b>, so the mapping stays valid for
    /// whatever that socket sends next. Tries each server, and each a few times, until one answers.
    /// </summary>
    public async Task<StunResult> GatherAsync(
        UdpClient socket,
        IReadOnlyList<IPEndPoint> servers,
        TimeSpan perServerTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(servers);

        foreach (IPEndPoint server in servers)
        {
            for (int attempt = 0; attempt < _attemptsPerServer; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                StunMessage request = StunMessage.CreateBindingRequest();
                await socket.SendAsync(request.ToBytes(), server, cancellationToken).ConfigureAwait(false);

                IPEndPoint? reflexive = await AwaitResponseAsync(
                    socket, request.TransactionId, perServerTimeout, cancellationToken).ConfigureAwait(false);
                if (reflexive is not null)
                {
                    return new StunResult(reflexive, server);
                }
            }
        }

        return new StunResult(null, null);
    }

    /// <summary>
    /// Wait for the response that matches <paramref name="transactionId"/>, ignoring anything else that lands
    /// on the socket, until the per-attempt window elapses.
    /// </summary>
    private static async Task<IPEndPoint?> AwaitResponseAsync(
        UdpClient socket, byte[] transactionId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                UdpReceiveResult received = await socket.ReceiveAsync(attemptCts.Token).ConfigureAwait(false);

                StunMessage? response = StunMessage.TryParse(received.Buffer);

                // Match the transaction id: a datagram from something else on this socket, or a stale reply to
                // a previous attempt, must not be read as this request's answer.
                if (response is { Type: StunMessageType.BindingSuccess }
                    && response.TransactionId.AsSpan().SequenceEqual(transactionId))
                {
                    return response.MappedAddress;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // This attempt timed out; the caller retries or moves on. A cancel from the caller's own token
            // propagates instead.
            return null;
        }
        catch (SocketException)
        {
            // ICMP port-unreachable surfaces here on some platforms; treat as "this server didn't answer".
            return null;
        }
    }

    /// <summary>Resolve host endpoints to IP endpoints, dropping any that do not resolve.</summary>
    public static async Task<IReadOnlyList<IPEndPoint>> ResolveAsync(
        IReadOnlyList<DnsEndPoint> servers, CancellationToken cancellationToken)
    {
        var resolved = new List<IPEndPoint>();
        foreach (DnsEndPoint server in servers)
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(
                    server.Host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
                if (addresses.Length > 0)
                {
                    resolved.Add(new IPEndPoint(addresses[0], server.Port));
                }
            }
            catch (SocketException)
            {
                // A server whose name will not resolve is simply skipped; the next one is tried.
            }
        }

        return resolved;
    }
}
