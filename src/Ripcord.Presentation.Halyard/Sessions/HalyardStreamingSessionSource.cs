using System.Net;
using Ripcord.Core.Consoles;
using Ripcord.Core.Sessions;
using Ripcord.Presentation.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Session;
using Ripcord.Protocol.Halyard.Transport;

namespace Ripcord.Presentation.Halyard.Sessions;

/// <summary>
/// The PlayStation implementation of <see cref="IStreamingSessionSource"/>: LAN when the console is on this
/// network, the account rendezvous when it is not.
/// </summary>
public sealed class HalyardStreamingSessionSource : IStreamingSessionSource
{
    private readonly HalyardSessionFactory _factory;
    private readonly Func<IProgress<string>?, HalyardAccountConsoleSession?> _accountSession;
    private readonly Func<bool> _accountAvailable;
    private readonly Func<string, bool> _isOnThisNetwork;

    /// <param name="accountSession">
    /// Built per attempt, not once: it holds a single-use push socket, so a connect that has been tried and
    /// torn down cannot be tried again on the same object — and connecting is the thing a user retries.
    /// Returns null when this build has no account tier at all.
    /// </param>
    /// <param name="isOnThisNetwork">
    /// Whether an address is on a network this machine is attached to. Injectable so the route decision can be
    /// tested at all: the real answer comes from the interface table, so a test that used it would pass or fail
    /// depending on whose machine it ran on.
    /// </param>
    /// <param name="accountAvailable">
    /// Whether this build has an account tier at all — asked when choosing a route.
    ///
    /// <para>
    /// Separate from <paramref name="accountSession"/> because deciding must not build one. The connector owns
    /// a single-use push socket, so constructing one merely to null-check it allocates a WebSocket and throws
    /// it away on every route decision.
    /// </para>
    /// </param>
    public HalyardStreamingSessionSource(
        HalyardSessionFactory factory,
        Func<IProgress<string>?, HalyardAccountConsoleSession?> accountSession,
        Func<bool> accountAvailable,
        string cryptoSource,
        Func<string, bool>? isOnThisNetwork = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _accountSession = accountSession ?? throw new ArgumentNullException(nameof(accountSession));
        _accountAvailable = accountAvailable ?? throw new ArgumentNullException(nameof(accountAvailable));
        _isOnThisNetwork = isOnThisNetwork ?? DefaultIsOnThisNetwork;

        Availability = factory.HasRealCrypto
            ? new StreamingAvailability(true, cryptoSource)
            : new StreamingAvailability(
                false,
                "Streaming needs the protocol's control-plane constants, which are normally bundled with the "
                + "build. This build either omitted them (BundleInteropConstants=false) or has a broken "
                + $"override.{Environment.NewLine}{Environment.NewLine}Detail: {cryptoSource}");
    }

    /// <inheritdoc />
    public StreamingAvailability Availability { get; }

    /// <inheritdoc />
    public Task<StreamingRouteChoice> ChooseRouteAsync(
        PairedConsole console, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);

        // Reachability is asked of the interface table, not of the console: this runs before anything is
        // sent, and a probe that waits for a timeout would put seconds in front of every connect just to
        // learn what the routing table already knows.
        if (_isOnThisNetwork(console.Host))
        {
            return Task.FromResult(new StreamingRouteChoice(
                StreamingRoute.Local, "This console is on your network."));
        }

        if (string.IsNullOrEmpty(console.CloudDeviceId))
        {
            // Paired by code rather than through the account, so the cloud has no name for it. Local is the
            // only route, and letting it try and fail says more than refusing here would.
            return Task.FromResult(new StreamingRouteChoice(
                StreamingRoute.Local,
                "This console isn't on your network, and it was paired without an account — so there's no "
                + "way to reach it from here. Trying its last known address."));
        }

        if (!_accountAvailable())
        {
            return Task.FromResult(new StreamingRouteChoice(
                StreamingRoute.Local,
                "This console isn't on your network, and this build can't connect through an account. "
                + "Trying its last known address."));
        }

        return Task.FromResult(new StreamingRouteChoice(
            StreamingRoute.Account, "Connecting through your account — this console isn't on your network."));
    }

    private static bool DefaultIsOnThisNetwork(string host)
        => IPAddress.TryParse(host, out IPAddress? address)
           && HalyardDatagramRegistrationTransport.SharesSubnetWithLocalInterface(address);

    /// <inheritdoc />
    public async Task<IStreamingSession> OpenAsync(
        PairedConsole console,
        StreamingRoute route,
        Func<bool, CancellationToken, Task<string?>>? loginPin,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);

        if (route == StreamingRoute.Local)
        {
            if (!IPAddress.TryParse(console.Host, out IPAddress? address))
            {
                throw new InvalidOperationException(
                    $"'{console.Host}' is not an address this console can be reached at.");
            }

            return _factory.Create(console.Id, address, loginPinProvider: loginPin);
        }

        HalyardAccountConsoleSession? account = _accountSession(progress)
            ?? throw new InvalidOperationException(
                "This build cannot connect through an account, so this console can only be reached on its "
                + "own network.");

        if (string.IsNullOrEmpty(console.CloudDeviceId))
        {
            throw new InvalidOperationException(
                "This console was paired without an account, so the account service has no name for it. "
                + "Re-pair it while signed in to reach it from other networks.");
        }

        HalyardAccountSessionResult result = await account
            .ConnectAsync(
                console.Host,
                console.CloudDeviceId,
                console.Platform.Equals("PS4", StringComparison.OrdinalIgnoreCase)
                    ? HalyardConsolePlatform.Ps4
                    : HalyardConsolePlatform.Ps5,
                loginPin,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded || result.Session is null)
        {
            throw new InvalidOperationException(
                result.FailureReason ?? "The account service did not open a connection to this console.");
        }

        // The association outlives the handshake and must die with the stream, so it is handed to the session
        // rather than left for the caller to remember -- see AccountRouteSession.
        return new AccountRouteSession(result.Session, result.Association);
    }
}
