using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Ripcord.Cloud.Halyard.Rendezvous;

/// <summary>What the caller must supply to identify the console and account for a WAN connect.</summary>
/// <param name="ConsoleDuid">The console's device unique id (from the cloud console list).</param>
/// <param name="AccountId">The signed-in account id.</param>
/// <param name="ClientType">The wire client-type tag; <c>Windows</c> unless a front end overrides it.</param>
public sealed record HalyardWanRequest(string ConsoleDuid, string AccountId, string ClientType = "Windows");

/// <summary>Tunables for the rendezvous, defaulted to what the observed flow uses.</summary>
public sealed class HalyardWanRendezvousOptions
{
    /// <summary>How long to keep offering and waiting for the console's OFFER before giving up.</summary>
    public TimeSpan RendezvousTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often to re-send our OFFER while waiting. The console can take several seconds to wake and join, and
    /// the observed client re-sends its OFFER (reqId 1) throughout that window rather than sending once.
    /// </summary>
    public TimeSpan OfferInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Optional progress sink for a harness. Receives short human-readable lines as the rendezvous proceeds —
    /// session created, session readback (does ours exist, has the console joined), offers, the console's
    /// answer. Invaluable on a live run, where "it timed out" is otherwise the whole diagnostic.
    /// </summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Drives the cloud rendezvous end to end and returns the console's reachable candidates — the last thing the
/// direct transport needs before it can connect off the LAN.
///
/// <para>
/// The sequence, which is the whole point of this type:
/// </para>
/// <list type="number">
///   <item><description>Create the account session.</description></item>
///   <item><description>Start the push channel (the console's OFFER arrives there) running in the background.</description></item>
///   <item><description>Send the wake/connect command so the console joins.</description></item>
///   <item><description>Gather our reflexive candidate <b>on the media socket</b> via STUN.</description></item>
///   <item><description>POST our OFFER (reflexive + LAN candidates), re-sending on an interval while we wait.</description></item>
///   <item><description>Return once the console's OFFER — its candidates — comes back over the push channel.</description></item>
/// </list>
///
/// <para>
/// The gather happens on the socket the caller will stream from, because a NAT binding is per source port: a
/// reflexive candidate discovered on any other socket would point at a port the media never uses. That is why
/// the media socket is a parameter rather than something this type creates.
/// </para>
/// </summary>
public sealed class HalyardWanRendezvous(
    IHalyardSignalingClient signaling,
    IReflexiveGatherer reflexiveGatherer,
    HalyardWanRendezvousOptions? options = null)
{
    private readonly IHalyardSignalingClient _signaling =
        signaling ?? throw new ArgumentNullException(nameof(signaling));

    private readonly IReflexiveGatherer _reflexiveGatherer =
        reflexiveGatherer ?? throw new ArgumentNullException(nameof(reflexiveGatherer));

    private readonly HalyardWanRendezvousOptions _options = options ?? new HalyardWanRendezvousOptions();

    /// <summary>
    /// Run the rendezvous. <paramref name="mediaSocket"/> is the socket the transport will stream from;
    /// <paramref name="pushChannel"/> is a not-yet-running push channel (the caller built it over a real or fake
    /// WebSocket); <paramref name="timeProvider"/> is for deterministic tests.
    /// </summary>
    /// <returns>
    /// A live connection carrying the console's candidates and owning the session + push channel until disposed.
    /// </returns>
    /// <exception cref="HalyardCloudException">If the console does not answer within the rendezvous timeout.</exception>
    public async Task<HalyardWanConnection> ConnectAsync(
        HalyardWanRequest request,
        UdpClient mediaSocket,
        HalyardPushChannel pushChannel,
        HalyardPushServerInfo pushServer,
        string accessToken,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mediaSocket);
        ArgumentNullException.ThrowIfNull(pushChannel);
        ArgumentNullException.ThrowIfNull(pushServer);
        TimeProvider clock = timeProvider ?? TimeProvider.System;

        // Owns the background push loop. Cancelled on failure here, and on the connection's disposal on success.
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Completed by the first OFFER the console sends us over the push channel.
        var consoleOffer = new TaskCompletionSource<IReadOnlyList<HalyardSignalingCandidate>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnSignaling(HalyardSignalingMessage message)
        {
            if (message.IsConsoleOffer)
            {
                consoleOffer.TrySetResult(message.Candidates);
            }
        }

        pushChannel.SignalingReceived += OnSignaling;

        // Start receiving before we trigger the console, so an OFFER that arrives the instant it joins is not
        // missed. The loop runs until lifetime is cancelled or the peer closes.
        Task pushLoop = pushChannel.RunAsync(pushServer, accessToken, lifetime.Token);

        try
        {
            // The session's message channel is bound to the account's live push connection at create time, so
            // the push WebSocket must be connected BEFORE the session is created — otherwise the session has no
            // sessionMessage sub-resource and our OFFER POST 404s. This ordering mirrors the vendor client
            // (push upgrade, then create). If the upgrade failed, awaiting this throws it here.
            await pushChannel.Connected.WaitAsync(cancellationToken).ConfigureAwait(false);

            string pushContextId = Guid.NewGuid().ToString();
            string sessionId = await _signaling
                .CreateSessionAsync(pushContextId, cancellationToken)
                .ConfigureAwait(false);
            Log($"session created: {sessionId}");

            // Confirm the session actually exists server-side before doing anything with it. A create that
            // returns an id but whose session is not then readable is the difference between "the console won't
            // answer" and "there is nothing to answer to", and only this readback tells them apart.
            await ReadbackAsync(sessionId, "after create", cancellationToken).ConfigureAwait(false);

            await _signaling.SendConnectCommandAsync(
                request.ConsoleDuid, request.AccountId, sessionId, request.ClientType, RandomSeeds(), cancellationToken)
                .ConfigureAwait(false);
            Log("wake command sent");

            IReadOnlyList<HalyardCandidate> ourCandidates =
                await BuildOurCandidatesAsync(mediaSocket, cancellationToken).ConfigureAwait(false);
            Log($"gathered {ourCandidates.Count} local candidate(s): {string.Join(", ", ourCandidates.Select(c => $"{c.Type} {c.Address}:{c.Port}"))}");

            IReadOnlyList<HalyardSignalingCandidate> consoleCandidates = await OfferUntilAnsweredAsync(
                request, sessionId, ourCandidates, consoleOffer.Task, clock, cancellationToken).ConfigureAwait(false);

            return new HalyardWanConnection(sessionId, consoleCandidates, _signaling, pushChannel, pushLoop, lifetime);
        }
        catch
        {
            pushChannel.SignalingReceived -= OnSignaling;
            await lifetime.CancelAsync().ConfigureAwait(false);
            await SafeAwaitAsync(pushLoop).ConfigureAwait(false);
            await pushChannel.DisposeAsync().ConfigureAwait(false);
            lifetime.Dispose();
            throw;
        }
    }

    /// <summary>Our candidates: the reflexive one first (as the console orders its own), then the LAN one.</summary>
    private async Task<IReadOnlyList<HalyardCandidate>> BuildOurCandidatesAsync(
        UdpClient mediaSocket, CancellationToken cancellationToken)
    {
        int localPort = ((IPEndPoint)mediaSocket.Client.LocalEndPoint!).Port;
        var candidates = new List<HalyardCandidate>();

        IPEndPoint? reflexive = await _reflexiveGatherer.GatherAsync(mediaSocket, cancellationToken).ConfigureAwait(false);
        if (reflexive is not null)
        {
            candidates.Add(new HalyardCandidate("STATIC", reflexive.Address.ToString(), reflexive.Port));
        }

        // The LAN candidate is always offered — on the same network it is the fast path, and off it it costs
        // nothing to include.
        if (LocalLanAddress() is { } lan)
        {
            candidates.Add(new HalyardCandidate("LOCAL", lan.ToString(), localPort));
        }

        return candidates;
    }

    /// <summary>
    /// POST our OFFER and wait for the console's, re-sending on <see cref="HalyardWanRendezvousOptions.OfferInterval"/>
    /// until the console answers or the overall timeout elapses.
    /// </summary>
    private async Task<IReadOnlyList<HalyardSignalingCandidate>> OfferUntilAnsweredAsync(
        HalyardWanRequest request,
        string sessionId,
        IReadOnlyList<HalyardCandidate> ourCandidates,
        Task<IReadOnlyList<HalyardSignalingCandidate>> consoleOffer,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        long deadline = clock.GetTimestamp() + (long)(_options.RendezvousTimeout.TotalSeconds * clock.TimestampFrequency);
        HalyardCloudException? lastOfferError = null;
        int round = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _signaling.SendOfferAsync(
                    sessionId, request.AccountId, request.ConsoleDuid, ourCandidates, cancellationToken)
                    .ConfigureAwait(false);
                Log($"offer #{round + 1} sent");
            }
            catch (HalyardCloudException ex)
            {
                // A just-created session can take a moment to become addressable across PSN's backend, so an
                // OFFER POST can transiently fail (a 404 in particular) before it settles. Keep re-offering
                // rather than aborting; the overall timeout still bounds it, and the last error is reported if
                // we never succeed.
                lastOfferError = ex;
                Log($"offer #{round + 1} failed: {ex.Message}");
            }

            // Every few rounds, read the session back so a live run can see whether the console has joined —
            // the difference between "our OFFER is fine, the console just isn't in yet" and a dead end.
            if (++round % 3 == 0)
            {
                await ReadbackAsync(sessionId, $"round {round}", cancellationToken).ConfigureAwait(false);
            }

            Task delay = Task.Delay(_options.OfferInterval, clock, cancellationToken);
            Task completed = await Task.WhenAny(consoleOffer, delay).ConfigureAwait(false);
            if (completed == consoleOffer)
            {
                return await consoleOffer.ConfigureAwait(false);
            }

            await delay.ConfigureAwait(false); // observe cancellation

            if (clock.GetTimestamp() >= deadline)
            {
                string reason = lastOfferError is null
                    ? "The console did not answer over the push channel within the rendezvous timeout. It may "
                      + "be offline, remote-wake may be disabled, or it rejected our OFFER."
                    : $"Never completed the OFFER exchange within the timeout; the last OFFER POST failed: "
                      + lastOfferError.Message;
                throw new HalyardCloudException(reason);
            }
        }
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    /// <summary>
    /// Read the session back and report what the account service sees: whether our session is present, how many
    /// members it has, and whether the console (a non-<c>REMOTE_PLAY</c> member) has joined. Never throws — a
    /// readback failure is diagnostic noise, not a reason to abort the connect.
    /// </summary>
    private async Task ReadbackAsync(string sessionId, string when, CancellationToken cancellationToken)
    {
        if (_options.Log is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<HalyardCloudSession> sessions =
                await _signaling.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            HalyardCloudSession? ours = sessions.FirstOrDefault(s =>
                string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

            if (ours is null)
            {
                Log($"readback {when}: OUR SESSION IS NOT PRESENT among {sessions.Count} account session(s) — create did not stick");
                return;
            }

            HalyardSessionMember[] members = ours.Members ?? [];
            bool consoleJoined = members.Any(m =>
                !string.Equals(m.Platform, "REMOTE_PLAY", StringComparison.OrdinalIgnoreCase));
            Log($"readback {when}: session present, {members.Length} member(s), console joined: {consoleJoined}");
        }
        catch (Exception ex)
        {
            Log($"readback {when}: failed ({ex.Message})");
        }
    }

    private static (string, string, string) RandomSeeds()
    {
        // Three 16-byte values the command carries. Their role is open [X] and unattached to the session
        // crypto (see docs), so random values of the right shape are as good as anything.
        static string Seed() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        return (Seed(), Seed(), Seed());
    }

    /// <summary>This host's primary LAN address, or null when it cannot be determined.</summary>
    private static IPAddress? LocalLanAddress()
    {
        try
        {
            // Connecting a UDP socket sends nothing but makes the OS choose the interface it would route from —
            // the address worth advertising, and more reliable than picking the first non-loopback NIC.
            //
            // The target only has to be somewhere the default route would carry a packet; nothing is ever sent
            // to it. RFC 5737 TEST-NET-1 is deliberate: it says "any off-link address" out loud, and it means
            // this does not quietly depend on a particular company's resolver staying reachable. This used to
            // be 8.8.8.8, which worked identically and read like infrastructure.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 65530));
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup path: the push loop's own failure/cancellation must not mask the original exception.
        }
    }
}
