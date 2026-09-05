using System.Net;
using System.Net.Sockets;
using Ripcord.Cloud.Halyard;
using Ripcord.Cloud.Halyard.Rendezvous;
using Ripcord.Core.Net.WebSockets;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Discovery;
using Ripcord.Protocol.Halyard.Session;
using Ripcord.Protocol.Halyard.Transport;

namespace Ripcord.Presentation.Halyard.Sessions;

/// <summary>
/// A streaming session opened over the account ("web") route, and the association it runs on.
///
/// <para>
/// Both are handed back because both must be disposed, and in this order: the session first, then the
/// association underneath it. A caller that dropped the association would leave the socket and its receive
/// loop alive for the life of the process.
/// </para>
/// </summary>
public sealed record HalyardAccountSessionResult(
    IStreamingSession? Session,
    IAsyncDisposable? Association,
    string? FailureReason)
{
    public bool Succeeded => Session is not null;

    public static HalyardAccountSessionResult Failed(string reason) => new(null, null, reason);
}

/// <summary>
/// Opens a streaming session against an already-paired console over the account route.
///
/// <para>
/// The counterpart of <see cref="Pairing.HalyardAccountConsolePairing"/>, and deliberately its shape: gather
/// the three cloud values the coordinator needs (a token, the push server, a socket), run the rendezvous, and
/// turn both halves' failures into a result rather than an exception.
/// </para>
///
/// <para>
/// <b>What differs from the LAN path.</b> A console reached this way serves its whole control plane on the
/// 9303 association the rendezvous just established — <c>/sess/init</c>, <c>/sess/ctrl</c> and the control
/// frames after them — and refuses TCP 9295. So the association is wrapped in a
/// <see cref="HalyardDatagramSessionControlChannel"/> and handed to the ordinary session factory. Nothing
/// above the channel changes, which is the whole point of that seam.
/// </para>
///
/// <para>
/// No registration crypto is resolved here, unlike pairing: a connect carries no seed, because the pairing
/// record it needs is already in the credential store.
/// </para>
/// </summary>
public sealed class HalyardAccountConsoleSession
{
    private readonly HalyardAccountGateway _gateway;
    private readonly HalyardSessionFactory _sessions;
    private readonly Func<IWebSocketChannel> _socketFactory;
    private readonly HalyardAccountPairingOptions _options;
    private readonly Action<HalyardPushChannel>? _observeChannel;

    /// <param name="socketFactory">
    /// Makes the push WebSocket for one attempt. Single-use: a channel that has been run and torn down cannot
    /// be run again, and connecting is a thing a user retries.
    /// </param>
    /// <param name="observeChannel">
    /// Called with the push channel before it runs, for diagnosis — the same hook pairing has, and for the
    /// same reason: "the console never joined" and "it joined and then went quiet" are different failures with
    /// the same symptom unless somebody is watching the raw frames.
    /// </param>
    public HalyardAccountConsoleSession(
        HalyardAccountGateway gateway,
        HalyardSessionFactory sessions,
        Func<IWebSocketChannel>? socketFactory = null,
        HalyardAccountPairingOptions? options = null,
        Action<HalyardPushChannel>? observeChannel = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _socketFactory = socketFactory ?? (() => new ClientWebSocketChannel());
        _options = options ?? new HalyardAccountPairingOptions();
        _observeChannel = observeChannel;
    }

    /// <param name="consoleHost">The console's address, which also keys its pairing record.</param>
    /// <param name="consoleDuid">The cloud device id, as the account's console list reports it.</param>
    /// <param name="loginPinProvider">Supplies the console login passcode when the console's user is locked.</param>
    public async Task<HalyardAccountSessionResult> ConnectAsync(
        string consoleHost,
        string consoleDuid,
        HalyardConsolePlatform platform,
        Func<bool, CancellationToken, Task<string?>>? loginPinProvider,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(consoleHost);
        ArgumentException.ThrowIfNullOrEmpty(consoleDuid);

        if (!_gateway.IsSignedIn)
        {
            return HalyardAccountSessionResult.Failed(
                "Sign in to your PlayStation Network account to connect over the account route.");
        }

        HalyardDatagramRegistrationTransport? transport = null;
        HalyardPushChannel? pushChannel = null;
        try
        {
            string token = await _gateway.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
            HalyardPushServerInfo pushServer = await _gateway.Cloud
                .GetPushServerAsync(cancellationToken)
                .ConfigureAwait(false);

            byte[] localHashedId = HalyardLocalHashedId.For(_gateway.ClientDeviceId);

            // Chosen before the OFFER, because the OFFER advertises the port the transport will speak from and
            // that is how the console ties the peer that offered to the peer that opens an association.
            int localPort = FreeUdpPort();
            string localAddress = HalyardDatagramRegistrationTransport.LocalAddressFor(
                IPAddress.Parse(consoleHost));

            var rendezvous = new HalyardAccountPairing(
                new HalyardCloudSignalingClient(_gateway.Cloud),

                // Never used on this path: a connect supplies its own transport factory below, and the
                // registration factory is only reached by PairAsync. Throwing rather than returning something
                // plausible keeps a future miswiring loud.
                _ => throw new InvalidOperationException(
                    "A connect does not register; this factory should never be called."),
                new byte[16],
                _options);

            // NOT `await using`: the cloud session must stay joined for as long as the stream runs. Leaving
            // it ends the session the console joined, and the console tears the association down with it --
            // observed live as a control connection that timed out straight after a rendezvous that had
            // otherwise gone perfectly. Ownership passes to the returned association.
            pushChannel = new HalyardPushChannel(_socketFactory());
            _observeChannel?.Invoke(pushChannel);

            HalyardAccountConnection connection = await rendezvous
                .ConnectAsync(
                    new HalyardAccountPairingRequest(
                        ConsoleId: consoleHost,
                        ConsoleHost: consoleHost,
                        ConsoleDuid: consoleDuid,
                        AccountId: _gateway.Account!.AccountId,
                        ClientDeviceId: ReadOnlyMemory<byte>.Empty,
                        Platform: platform,
                        LocalHashedId: localHashedId,
                        LocalEndpoint: (localAddress, localPort)),
                    pushChannel,
                    pushServer,
                    token,
                    context =>
                    {
                        transport = new HalyardDatagramRegistrationTransport(
                            ConsoleEndpoint(context),
                            context.LocalHashedId,
                            context.ConsoleHashedId,
                            new HalyardDatagramControlOptions
                            {
                                Log = _options.Log,
                                HelloAddressing = _options.HelloAddressing,
                            },
                            endpoint => new HalyardUdpDatagramTransport(endpoint, localPort));
                        return transport;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!connection.Succeeded || connection.Channel is null)
            {
                await DisposeQuietlyAsync(connection.SessionLifetime).ConfigureAwait(false);
                await DisposeQuietlyAsync(transport).ConfigureAwait(false);
                await DisposeQuietlyAsync(pushChannel).ConfigureAwait(false);
                return HalyardAccountSessionResult.Failed(
                    connection.FailureReason ?? "The account route did not open a control association.");
            }

            // The endpoints are informational on this path: the control plane rides the association, and the
            // stream endpoint is the second negotiated connection, which this does not open yet.
            var parameters = new HalyardConnectionParameters(
                ConsoleId: consoleHost,
                ControlEndpoint: new IPEndPoint(
                    IPAddress.Parse(consoleHost), HalyardDatagramRegistrationTransport.Port),
                StreamEndpoint: new IPEndPoint(
                    IPAddress.Parse(consoleHost), HalyardSessionFactory.DefaultStreamPort));

            // Longer than the LAN default: this console gates its control plane on the rendezvous completing,
            // and one was measured taking about twenty seconds to send its signaling ACCEPT -- which the
            // default deadline turned into a failure on a link that was working.
            IStreamingSession session = _sessions.Create(
                parameters,
                new HalyardDatagramSessionControlChannel(connection.Channel),
                loginPinProvider,
                ControlPlaneDeadline);

            // Everything the stream needs alive, disposed in the order it was built.
            return new HalyardAccountSessionResult(
                session,
                new AccountSessionResources(connection.SessionLifetime, transport, pushChannel),
                null);
        }
        catch (HalyardCloudException ex)
        {
            await DisposeQuietlyAsync(transport).ConfigureAwait(false);
            await DisposeQuietlyAsync(pushChannel).ConfigureAwait(false);
            return HalyardAccountSessionResult.Failed(
                $"PlayStation Network refused the request: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            await DisposeQuietlyAsync(transport).ConfigureAwait(false);
            await DisposeQuietlyAsync(pushChannel).ConfigureAwait(false);
            return HalyardAccountSessionResult.Failed($"Couldn't reach PlayStation Network: {ex.Message}");
        }
    }

    /// <summary>
    /// Everything an account-route session needs kept alive: the cloud session membership and push loop, the
    /// association, and the push socket. Disposed newest-first.
    /// </summary>
    private sealed class AccountSessionResources(
        IAsyncDisposable? sessionLifetime,
        IAsyncDisposable? transport,
        IAsyncDisposable? pushChannel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await DisposeQuietlyAsync(transport).ConfigureAwait(false);
            await DisposeQuietlyAsync(sessionLifetime).ConfigureAwait(false);
            await DisposeQuietlyAsync(pushChannel).ConfigureAwait(false);
        }
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already on a failure path; a teardown problem must not replace the reason we got here.
        }
    }

    /// <summary>How long the control plane may take on this route. See where it is passed.</summary>
    private static readonly TimeSpan ControlPlaneDeadline = TimeSpan.FromSeconds(60);

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>Prefer the console's own LOCAL candidate when it matches where we think it is.</summary>
    private static IPEndPoint ConsoleEndpoint(HalyardAccountTransportContext context)
    {
        foreach (HalyardSignalingCandidate candidate in context.ConsoleOffer.Candidates)
        {
            if (candidate.Type == "LOCAL"
                && IPAddress.TryParse(candidate.Address, out IPAddress? local)
                && string.Equals(candidate.Address, context.ConsoleHost, StringComparison.Ordinal))
            {
                return new IPEndPoint(local, candidate.Port);
            }
        }

        return new IPEndPoint(
            IPAddress.Parse(context.ConsoleHost), HalyardDatagramRegistrationTransport.Port);
    }
}
