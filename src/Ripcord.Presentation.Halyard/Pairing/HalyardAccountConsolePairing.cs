using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Ripcord.Cloud.Halyard;
using Ripcord.Cloud.Halyard.Rendezvous;
using Ripcord.Core.Net.WebSockets;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Halyard.Consoles;
using Ripcord.Presentation.Pairing;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Discovery;
using Ripcord.Protocol.Halyard.Transport;

namespace Ripcord.Presentation.Halyard.Pairing;

/// <summary>
/// Account ("web") pairing against a PlayStation console: no code off the console's screen, because the console
/// delivers the registration seed itself, encrypted, over the account service.
///
/// <para>
/// Almost all of this is <see cref="HalyardAccountPairing"/>'s job — generate the ephemeral key material, listen
/// on the push channel before triggering the console, recover the seed from <c>customData1</c>, register. What is
/// left here is what a presentation seam owes: resolving the crypto from the same chain the code route uses,
/// gathering the three cloud values the coordinator needs (a token, the push server, a socket), and turning both
/// halves' failures into a result rather than an exception.
/// </para>
///
/// <para>
/// The socket is created per attempt through a factory rather than held, because it is single-use: a push
/// WebSocket that has been run and torn down cannot be run again, and pairing is a thing a user retries.
/// </para>
/// </summary>
public sealed class HalyardAccountConsolePairing : IAccountConsolePairing
{
    private readonly HalyardAccountGateway _gateway;
    private readonly IHalyardRegistrationCipherResolver _cipherResolver;
    private readonly Func<IWebSocketChannel> _socketFactory;
    private readonly HalyardAccountPairingOptions _options;
    private readonly Action<HalyardPushChannel>? _observeChannel;

    /// <param name="socketFactory">
    /// Makes the push WebSocket for one attempt. Defaults to the real client; a test or harness substitutes a
    /// scripted socket here, which is what makes the whole flow drivable from captured frames.
    /// </param>
    /// <param name="observeChannel">
    /// Called with the push channel for each attempt, immediately after it is built and before it runs.
    ///
    /// <para>
    /// This exists for diagnosis, and it earned its place the hard way: the first live run reported only that
    /// the seed never arrived, which is the least informative thing it could have said. The console publishes
    /// <c>customData1</c> only after it <em>joins</em> the session, and the join is announced on this same
    /// channel — so "did the console join?" and "did it join and then not publish?" are different failures with
    /// the same symptom unless somebody is watching the raw frames. The channel raises every one via
    /// <see cref="HalyardPushChannel.FrameReceived"/>; nothing in the app subscribes, and the harness does.
    /// </para>
    /// </param>
    public HalyardAccountConsolePairing(
        HalyardAccountGateway gateway,
        IHalyardRegistrationCipherResolver? cipherResolver = null,
        Func<IWebSocketChannel>? socketFactory = null,
        HalyardAccountPairingOptions? options = null,
        Action<HalyardPushChannel>? observeChannel = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _cipherResolver = cipherResolver ?? new HalyardRegistrationCipherResolver();
        _socketFactory = socketFactory ?? (() => new ClientWebSocketChannel());
        _options = options ?? new HalyardAccountPairingOptions();
        _observeChannel = observeChannel;
    }

    /// <summary>
    /// Write down which console we are about to command, and what the account says about it.
    ///
    /// <para>
    /// <b>Because the duid is matched by NAME.</b> The flow resolves it with
    /// <c>CloudConsoleMatch.ResolveId</c> against the console's display name, so a console whose account
    /// record is named differently — or an account holding more than one console with similar names —
    /// produces a command the cloud accepts, addressed to something that is not there. The symptom is
    /// identical to a console that will not wake: nothing joins, and nothing says why.
    /// </para>
    ///
    /// <para>
    /// Only runs when a trace is on, because it costs a cloud round trip to answer a question nobody is
    /// asking the rest of the time. Never fails the pairing.
    /// </para>
    /// </summary>
    private async Task LogTargetAsync(AccountPairingRequest request, CancellationToken cancellationToken)
    {
        if (_options.Log is null)
        {
            return;
        }

        void Log(string line) => _options.Log!(line);

        try
        {
            IReadOnlyList<HalyardConsoleClient> consoles = await _gateway.Cloud
                .ListConsolesAsync(cancellationToken)
                .ConfigureAwait(false);

            Log($"pairing target: platform={request.Family.Key} duid={request.CloudDeviceId}");
            Log($"the account lists {consoles.Count} console(s):");

            foreach (HalyardConsoleClient console in consoles)
            {
                bool isTarget = string.Equals(console.Duid, request.CloudDeviceId, StringComparison.Ordinal);

                // The MODES, not just whether the list is non-empty. CanWake collapses
                // wakeupEnabledPowerModes to a bool, and the spec records values like
                // ["networkStandby", "mainOnStandby"] — a console that permits waking from one standby mode
                // and is currently in the other is wakeable by that bool and not in fact wakeable. The
                // features list is here for the same reason: "remotePlay is enabled" is one entry in it.
                string modes = console.Device.WakeupEnabledPowerModes is { Length: > 0 } w
                    ? string.Join(",", w)
                    : "(none)";

                string features = console.Device.EnabledFeatures is { Length: > 0 } f
                    ? string.Join(",", f)
                    : "(none)";

                Log($"  {(isTarget ? "->" : "  ")} name=\"{console.Device.Name}\" platform={console.Platform} "
                    + $"remotePlay={console.RemotePlayEnabled} duid={console.Duid}");
                Log($"       wakeupEnabledPowerModes=[{modes}]");
                Log($"       enabledFeatures=[{features}]");
            }

            if (!consoles.Any(c => string.Equals(c.Duid, request.CloudDeviceId, StringComparison.Ordinal)))
            {
                Log("NONE of them is the duid being commanded - the name match resolved to something the "
                    + "account does not list, so the command will be accepted and go nowhere");
            }
        }
        catch (Exception ex)
        {
            Log($"could not read the account console list: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Where to open the control transport.
    ///
    /// <para>
    /// The console's <c>LOCAL</c> candidate when it names the address we already reached it on, which is the
    /// same-network case and the one that needs no hole-punch. Otherwise the address the caller gave us, on
    /// the control port — a console we found by LAN discovery is reachable there whatever it advertises, and
    /// preferring its reflexive candidate would send LAN traffic out to the internet and back.
    /// </para>
    /// </summary>
    /// <summary>
    /// A UDP port nothing is currently using. Bound and released rather than held, because the socket that
    /// will use it is created later, behind the registration factory.
    /// </summary>
    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

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

    public AccountPairingAvailability CheckAvailability(ConsoleFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);

        if (!_gateway.CanSignIn)
        {
            return new AccountPairingAvailability(false,
                "This build has no PlayStation Network client credential, so it can only pair with the code "
                + "shown on the console.");
        }

        HalyardRegistrationCipherResolution resolved = _cipherResolver.Resolve(family.ToHalyardPlatform());
        if (!resolved.Cipher.IsAvailable)
        {
            return new AccountPairingAvailability(false, resolved.Source);
        }

        // The seed arrives field-encrypted under this key. A source that yielded a cipher but no context key
        // would fail much later, as a seed that decrypts to noise — so it is refused here instead.
        return resolved.ContextKey.Length == 16
            ? new AccountPairingAvailability(true, resolved.Source)
            : new AccountPairingAvailability(false,
                $"The registration constants from {resolved.Source} carry no field context key, which account "
                + "pairing needs to read the console's reply.");
    }

    public async Task<ConsoleRegistrationResult> PairAsync(
        AccountPairingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        HalyardConsolePlatform platform = request.Family.ToHalyardPlatform();
        HalyardRegistrationCipherResolution resolved = _cipherResolver.Resolve(platform);
        if (!resolved.Cipher.IsAvailable || resolved.ContextKey.Length != 16)
        {
            return new ConsoleRegistrationResult(false, CheckAvailability(request.Family).Detail, null);
        }

        if (!_gateway.IsSignedIn)
        {
            return new ConsoleRegistrationResult(false,
                "Sign in to your PlayStation Network account to pair without a code.", null);
        }

        await LogTargetAsync(request, cancellationToken).ConfigureAwait(false);

        try
        {
            string token = await _gateway.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
            HalyardPushServerInfo pushServer = await _gateway.Cloud
                .GetPushServerAsync(cancellationToken)
                .ConfigureAwait(false);

            byte[] localHashedId = HalyardLocalHashedId.For(_gateway.ClientDeviceId);

            // Chosen before the OFFER, because the OFFER must advertise the port the transport will actually
            // speak from — the vendor's does, and it is how the console ties the peer that offered to the peer
            // that opens a transport. Picked by binding and releasing, so there is a small window in which
            // something else could take it; the alternative is holding a socket open across a seam that does
            // not want one, and a port collision here fails loudly at the prelude rather than silently.
            int localPort = FreeUdpPort();
            string localAddress = HalyardDatagramRegistrationTransport.LocalAddressFor(
                IPAddress.Parse(request.Host));

            var pairing = new HalyardAccountPairing(
                new HalyardCloudSignalingClient(_gateway.Cloud),

                // The account route registers over UDP 9303, not the PIN route's TCP 9295 — a console reached
                // this way answers the latter with its generic 403. The transport can only be built here,
                // once the console's OFFER has said where it is and what it calls itself.
                context => new HalyardRegistrationClient(
                    resolved.Cipher,
                    new HalyardDatagramRegistrationTransport(
                        ConsoleEndpoint(context),
                        context.LocalHashedId,
                        context.ConsoleHashedId,
                        new HalyardDatagramControlOptions
                        {
                            Log = _options.Log,
                            HelloAddressing = _options.HelloAddressing,
                        },
                        endpoint => new HalyardUdpDatagramTransport(endpoint, localPort))),
                resolved.ContextKey,
                _options);

            // Built here, not by the coordinator: the coordinator only runs and stops the receive loop, by
            // design, so that a caller can hand it a channel it is replaying frames through. Disposing the
            // channel disposes the socket with it, which is why the socket is not held separately.
            await using var pushChannel = new HalyardPushChannel(_socketFactory());
            _observeChannel?.Invoke(pushChannel);

            HalyardRegistrationResult result = await pairing
                .PairAsync(
                    new HalyardAccountPairingRequest(
                        ConsoleId: request.Host,
                        ConsoleHost: request.Host,
                        ConsoleDuid: request.CloudDeviceId,
                        AccountId: request.AccountId,

                        // 32 bytes, matching the code route exactly — see HalyardConsoleRegistrar, where the
                        // discrepancy with HalyardRegistrationRequest's own "16-byte" doc comment is recorded.
                        // The two routes differ in how the transport key is obtained and in nothing else, so a
                        // different length here would be a second unexplained wire difference.
                        ClientDeviceId: RandomNumberGenerator.GetBytes(32),
                        Platform: platform,
                        LocalHashedId: localHashedId,
                        LocalEndpoint: (localAddress, localPort)),
                    pushChannel,
                    pushServer,
                    token,
                    cancellationToken)
                .ConfigureAwait(false);

            return result.Succeeded && result.Record is not null
                ? new ConsoleRegistrationResult(true, null, result.Record.Serialize())
                : new ConsoleRegistrationResult(false, result.FailureReason ?? "Account pairing failed.", null);
        }
        catch (HalyardCloudException ex)
        {
            // The account service refused something — an expired session, a console the account no longer
            // owns. Ordinary enough that the user should read it rather than see a crash.
            return new ConsoleRegistrationResult(false, $"PlayStation Network refused the request: {ex.Message}", null);
        }
        catch (HttpRequestException ex)
        {
            return new ConsoleRegistrationResult(false, $"Couldn't reach PlayStation Network: {ex.Message}", null);
        }
    }
}
