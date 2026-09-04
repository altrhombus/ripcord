using System.Net;
using System.Net.Sockets;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Discovery;

namespace Ripcord.Protocol.Halyard.Transport;

/// <summary>
/// The account ("web"/no-PIN) route's registration transport: the same HTTP request the PIN route sends,
/// carried over the UDP 9303 chunk framing instead of TCP 9295.
///
/// <para>
/// Why the account route cannot simply use the TCP one: a console reached through the cloud rendezvous serves
/// its whole control plane — <c>rgst</c>, <c>init</c> and <c>ctrl</c> — on 9303, and answers a TCP 9295
/// registration with <c>HTTP 403 / RP-Application-Reason 80108bff</c>, its generic refusal. That was observed
/// live before this existed.
/// </para>
///
/// <para>
/// The two <c>localHashedId</c> values are the transport's real prerequisite: the prelude names both peers by
/// the ids they published in their signaling OFFERs, so account registration depends on the candidate exchange
/// having happened, not only on holding the delivered seed.
/// </para>
/// </summary>
public sealed class HalyardDatagramRegistrationTransport : IHalyardRegistrationTransport, IAsyncDisposable
{
    /// <summary>The account route's control port. A required on-wire value.</summary>
    public const int Port = 9303;

    private readonly IPEndPoint _console;
    private readonly ReadOnlyMemory<byte> _localHashedId;
    private readonly ReadOnlyMemory<byte> _consoleHashedId;
    private readonly HalyardDatagramControlOptions _options;
    private readonly Func<IPEndPoint, IHalyardDatagramTransport> _transportFactory;
    private HalyardDatagramControlChannel? _channel;

    /// <param name="console">
    /// Where to reach the console — from its OFFER's <c>LOCAL</c> candidate on a shared network, or its
    /// reflexive one otherwise.
    /// </param>
    /// <param name="transportFactory">
    /// Makes the socket. Defaulted to a real one; a test supplies a scripted peer.
    /// </param>
    public HalyardDatagramRegistrationTransport(
        IPEndPoint console,
        ReadOnlyMemory<byte> localHashedId,
        ReadOnlyMemory<byte> consoleHashedId,
        HalyardDatagramControlOptions? options = null,
        Func<IPEndPoint, IHalyardDatagramTransport>? transportFactory = null)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _localHashedId = localHashedId;
        _consoleHashedId = consoleHashedId;
        _options = options ?? new HalyardDatagramControlOptions();
        _transportFactory = transportFactory ?? (endpoint => new HalyardUdpDatagramTransport(endpoint));
    }

    /// <summary>
    /// Put our opening Init on the wire, so that when the console reacts to our ACCEPT it finds an association
    /// we opened rather than opening its own. Does not wait for an answer — it cannot, because the answer
    /// depends on signaling this call is meant to precede.
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        _channel ??= new HalyardDatagramControlChannel(
            _transportFactory(_console), _console, _localHashedId, _consoleHashedId, _options);

        await _channel.BeginAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ExchangeAsync(
        HalyardRegistrationRequest request, byte[] encryptedBody, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The HOST header is the client's own address, as on the TCP path — the console validates it as a
        // dotted quad. There is no connect to learn it from here, so ask the routing table which interface
        // would be used to reach this console.
        string clientIp = LocalAddressFor(_console.Address);
        byte[] requestBytes = HalyardRegistrationMessage.BuildRequest(request, clientIp, encryptedBody);

        // Begun already if the caller prepared us, which it should have; beginning here too is harmless
        // because the association only opens once.
        await PrepareAsync(cancellationToken).ConfigureAwait(false);

        return await _channel!.ExchangeAsync(requestBytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Which of this machine's addresses would be used to reach <paramref name="console"/>.
    ///
    /// <para>
    /// Connecting a UDP socket sends nothing; it just makes the OS pick the interface it would route from,
    /// which is more reliable than taking the first non-loopback address on a machine with a VPN or a virtual
    /// switch. Falls back to loopback, which will fail visibly at the console rather than silently.
    /// </para>
    /// </summary>
    public static string LocalAddressFor(IPAddress console)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(console, Port);
            IPAddress? local = (probe.LocalEndPoint as IPEndPoint)?.Address;
            if (local is not null && local.IsIPv4MappedToIPv6)
            {
                local = local.MapToIPv4();
            }

            return local?.ToString() ?? IPAddress.Loopback.ToString();
        }
        catch (SocketException)
        {
            return IPAddress.Loopback.ToString();
        }
    }
}
