using Ripcord.Cloud.Halyard;
using Ripcord.Cloud.Halyard.Rendezvous;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

namespace Ripcord.Protocol.Halyard.Discovery;

/// <summary>What identifies the console and account for an account ("web"/no-PIN) pairing.</summary>
/// <param name="ConsoleId">The stored console id (for the pairing record).</param>
/// <param name="ConsoleHost">The console's reachable host — from LAN discovery or a WAN rendezvous candidate —
/// for the direct <c>/sess/rgst</c> POST.</param>
/// <param name="ConsoleDuid">The console's device unique id (from the cloud console list).</param>
/// <param name="AccountId">The signed-in account id.</param>
/// <param name="ClientDeviceId">This device's id (the RP-Did material).</param>
/// <param name="LocalEndpoint">
/// The address and UDP port our control transport will speak from, advertised as our <c>LOCAL</c> candidate.
/// The vendor's OFFER lists the very port its 9303 traffic then originates from, so the console can correlate
/// the peer that offered with the peer that opens a transport. Null omits the candidate.
/// </param>
/// <param name="LocalHashedId">
/// Our own 20-byte signaling id. Published in our OFFER and re-asserted in the 9303 control prelude, which is
/// why it belongs to the whole flow rather than to the transport alone — the two must agree. Empty falls back
/// to the TCP registration path, i.e. the PIN route's transport, which an account console refuses.
/// </param>
public sealed record HalyardAccountPairingRequest(
    string ConsoleId,
    string ConsoleHost,
    string ConsoleDuid,
    string AccountId,
    ReadOnlyMemory<byte> ClientDeviceId,
    HalyardConsolePlatform Platform = HalyardConsolePlatform.Ps5,
    string ClientType = "Windows",
    ReadOnlyMemory<byte> LocalHashedId = default,
    (string Address, int Port)? LocalEndpoint = null);

/// <summary>
/// What the account route's transport needs, known only once the console has offered.
/// </summary>
/// <param name="ConsoleOffer">The console's OFFER, in full — its candidates and the id it names itself by.</param>
/// <param name="ConsoleHost">
/// The address the caller already had for the console, from LAN discovery. Preferred over a candidate when
/// the two agree, because it is the one we know is reachable from here.
/// </param>
/// <param name="LocalEndpoint">
/// The address and UDP port our control transport will speak from, advertised as our <c>LOCAL</c> candidate.
/// The vendor's OFFER lists the very port its 9303 traffic then originates from, so the console can correlate
/// the peer that offered with the peer that opens a transport. Null omits the candidate.
/// </param>
/// <param name="LocalHashedId">Our own signaling id, as announced in our OFFER.</param>
/// <param name="ConsoleHashedId">The console's, from its OFFER.</param>
public sealed record HalyardAccountTransportContext(
    HalyardSignalingMessage ConsoleOffer,
    string ConsoleHost,
    ReadOnlyMemory<byte> LocalHashedId,
    ReadOnlyMemory<byte> ConsoleHashedId);

/// <summary>Tunables for account pairing.</summary>
public sealed class HalyardAccountPairingOptions
{
    /// <summary>How long to wait for the console to publish <c>customData1</c> (the seed) after the command.</summary>
    public TimeSpan SeedTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait for the console's OFFER after it joins. Separate from the seed wait because they are
    /// separate events — the console publishes the seed and offers its candidates independently, and either
    /// can arrive first.
    /// </summary>
    public TimeSpan OfferTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional progress sink for a harness (push connected, session, command, seed, register).</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Drives account ("web"/no-PIN) pairing end to end: the console delivers the registration seed encrypted over
/// the cloud, and this coordinates receiving it and turning it into a pairing record.
///
/// <para>
/// The sequence — the whole point of this type:
/// </para>
/// <list type="number">
///   <item><description>Generate ephemeral <c>data1</c>/<c>data2</c> (the seed-delivery key/material).</description></item>
///   <item><description>Start the push channel and subscribe to <c>customData1</c> <b>before</b> triggering the
///   console, so the seed cannot arrive unheard.</description></item>
///   <item><description>Create the session and send the connect command carrying <c>data1</c>/<c>data2</c>.</description></item>
///   <item><description>The console field-encrypts the seed with them and publishes it as <c>customData1</c>;
///   recover it (<see cref="HalyardAccountSeedDelivery"/>).</description></item>
///   <item><description>Run the direct <c>/sess/rgst</c> registration with that seed and return the pairing
///   record.</description></item>
/// </list>
///
/// <para>
/// The push channel is passed in not-yet-running (the caller built it over a real or fake WebSocket), which is
/// what lets the whole flow be driven from captured/scripted frames in tests. Its socket remains the caller's
/// to dispose; this type only runs and then stops the receive loop.
/// </para>
/// </summary>
/// <param name="registration">
/// Builds the registration client for the console once its OFFER has told us where it is and what it calls
/// itself. A factory rather than a ready-made client because the account route's transport cannot be
/// constructed until then: the 9303 prelude names both peers by their signaling ids, so the console's
/// <c>localHashedId</c> — which arrives only in its OFFER — is an input to the transport, not to the request.
/// </param>
public sealed class HalyardAccountPairing(
    IHalyardSignalingClient signaling,
    Func<HalyardAccountTransportContext, IHalyardRegistration> registration,
    ReadOnlyMemory<byte> contextKey,
    HalyardAccountPairingOptions? options = null)
{
    private readonly IHalyardSignalingClient _signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
    private readonly Func<HalyardAccountTransportContext, IHalyardRegistration> _registration =
        registration ?? throw new ArgumentNullException(nameof(registration));
    private readonly byte[] _contextKey = contextKey.Length == 16
        ? contextKey.ToArray()
        : throw new ArgumentException("Context key must be 16 bytes.", nameof(contextKey));
    private readonly HalyardAccountPairingOptions _options = options ?? new HalyardAccountPairingOptions();

    /// <summary>
    /// Pair an account console. <paramref name="pushChannel"/> is a not-yet-running channel over a WebSocket
    /// the caller owns; <paramref name="pushServer"/> and <paramref name="accessToken"/> drive its upgrade.
    /// </summary>
    public async Task<HalyardRegistrationResult> PairAsync(
        HalyardAccountPairingRequest request,
        HalyardPushChannel pushChannel,
        HalyardPushServerInfo pushServer,
        string accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pushChannel);
        ArgumentNullException.ThrowIfNull(pushServer);

        (byte[] data1, byte[] data2) = HalyardAccountSeedDelivery.GenerateEphemeralKeyMaterial();

        // Set as soon as the session exists, so the teardown below can leave whatever we joined — including on
        // the failure paths, which is where it matters.
        string? joinedSessionId = null;

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Completed by the first customData1 that decrypts under our data1/data2. A foreign or malformed one is
        // ignored so a stray frame cannot resolve the wait with garbage.
        var seed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCustomData1(string customData1)
        {
            try
            {
                seed.TrySetResult(HalyardAccountSeedDelivery.RecoverSeed(data1, data2, customData1, _contextKey));
            }
            catch (FormatException) { /* not a valid double-base64 customData1 for us — keep waiting */ }
            catch (ArgumentException) { /* wrong length after decode — keep waiting */ }
        }

        // The console's OFFER, which carries where it is and the id it will name itself by in the prelude.
        var offer = new TaskCompletionSource<HalyardSignalingMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnSignaling(HalyardSignalingMessage message)
        {
            // Its own OFFER, not an answer to ours and not one of the RESULTs that follow: the exchange is
            // symmetric, so both sides OFFER and the console's is the one carrying its candidates.
            if (message.Action == "OFFER" && message.LocalHashedId is { Length: > 0 })
            {
                offer.TrySetResult(message);
            }
        }

        // The console joining is the earliest moment we may message it, and the moment before it starts its
        // own control association. Everything the client wants to get in early hangs off this.
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnJoined() => joined.TrySetResult();

        pushChannel.CustomData1Received += OnCustomData1;
        pushChannel.SignalingReceived += OnSignaling;
        pushChannel.ConsoleJoined += OnJoined;

        // Start receiving before triggering the console, so a customData1 published the instant it joins is not
        // missed. The loop runs until lifetime is cancelled or the peer closes.
        Task pushLoop = pushChannel.RunAsync(pushServer, accessToken, lifetime.Token);

        try
        {
            // The session's message channel binds to the live push connection at create time, so the push
            // WebSocket must be up before the session is created (mirrors the vendor ordering). A failed upgrade
            // surfaces here.
            await pushChannel.Connected.WaitAsync(cancellationToken).ConfigureAwait(false);
            Log("push channel connected");

            string sessionId = await _signaling
                .CreateSessionAsync(Guid.NewGuid().ToString(), cancellationToken)
                .ConfigureAwait(false);
            joinedSessionId = sessionId;
            Log($"session created: {sessionId}");

            await _signaling.SendConnectCommandAsync(
                request.ConsoleDuid, request.AccountId, sessionId, request.ClientType,
                (Convert.ToBase64String(data1), Convert.ToBase64String(data2), string.Empty), cancellationToken)
                .ConfigureAwait(false);
            Log("connect command sent (data1/data2)");

            // The console joining is the first moment it can be messaged at all — a sessionMessage only
            // reaches a member. It is NOT, however, the moment to offer: sending ours before the console's
            // OFFER breaks the exchange, because on this route the console initiates the signaling (its OFFER
            // arrives unprompted, with its own reqId) and we are the responder. Tried live; the console
            // answered by TERMINATE-ing and never opened a control association at all.
            if (!request.LocalHashedId.IsEmpty)
            {
                try
                {
                    await joined.Task.WaitAsync(_options.OfferTimeout, cancellationToken).ConfigureAwait(false);
                    Log("console joined the session");
                }
                catch (TimeoutException)
                {
                    Log("the console never joined the session");
                }
            }

            byte[] recoveredSeed;
            try
            {
                recoveredSeed = await seed.Task.WaitAsync(_options.SeedTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new HalyardRegistrationResult(false,
                    "The console did not publish the registration seed (customData1) in time.", null);
            }

            Log("registration seed recovered from customData1");

            HalyardSignalingMessage consoleOffer;
            try
            {
                consoleOffer = await offer.Task.WaitAsync(_options.OfferTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new HalyardRegistrationResult(false,
                    "The console never offered its candidates, so there is no address to register against.", null);
            }

            Log($"console OFFER received ({consoleOffer.Candidates.Count} candidates)");

            var context = new HalyardAccountTransportContext(
                consoleOffer, request.ConsoleHost, request.LocalHashedId, consoleOffer.LocalHashedId!);
            IHalyardRegistration registration = _registration(context);

            // Our Init goes out FIRST — before the RESULT, the OFFER and the ACCEPT below.
            //
            // The side that opens the association is the side that may open connections on it, and the console
            // opens its own within tens of milliseconds of learning our candidate. Every earlier ordering lost
            // that race, leaving us the responder on an association the console then never opened a connection
            // on.
            //
            // The open question this tests: the prelude names both peers, and the console has not yet seen our
            // OFFER, so it does not know our SenderId. If it validates only that PeerId is itself — which is the
            // plausible reading, since SenderId is the *claim* and PeerId is the *check* — this works and we
            // take the initiator role. If it validates SenderId too, this cannot work from here at all, and
            // that is worth knowing plainly rather than assuming. **[X]**
            try
            {
                await registration.PrepareAsync(cancellationToken).ConfigureAwait(false);
                Log("control association opened (before the signaling answer)");
            }
            catch (Exception ex)
            {
                Log($"could not open the control association: {ex.Message}");
            }


            // Announce ourselves — now, and not a moment earlier. A sessionMessage may only be sent to a
            // member, so this 404s until the console has joined, and the console's OFFER is exactly what tells
            // us it has. (Observed as a live 404 when this was sent right after the command.) It must still
            // precede the prelude: the console has to have seen the id and port we are about to speak from.
            if (!request.LocalHashedId.IsEmpty)
            {
                // The negotiation is a real offer/accept, not two independent advertisements — the capture
                // shows every message acknowledged by a RESULT carrying the sender's reqId, and the client
                // answering the console's OFFER with an ACCEPT that names the console's stream id. Stopping
                // after our own OFFER leaves the console waiting, and it never opens its side: observed live
                // as five unanswered preludes with the console silent.
                await _signaling.SendResultAsync(
                    sessionId, request.AccountId, request.ConsoleDuid, consoleOffer.ReqId, cancellationToken)
                    .ConfigureAwait(false);

                IReadOnlyList<HalyardCandidate> ours = request.LocalEndpoint is { } advertised
                    ? [new HalyardCandidate("LOCAL", advertised.Address, advertised.Port)]
                    : [];

                await _signaling.SendOfferAsync(
                    sessionId, request.AccountId, request.ConsoleDuid, ours, cancellationToken,
                    request.LocalHashedId).ConfigureAwait(false);

                HalyardSignalingCandidate? path = PreferredCandidate(consoleOffer, request.ConsoleHost);
                if (path is not null && request.LocalEndpoint is { } local)
                {
                    await _signaling.SendAcceptAsync(
                        sessionId, request.AccountId, request.ConsoleDuid,
                        reqId: OurAcceptReqId, sid: OurStreamId, peerSid: consoleOffer.Sid,
                        new HalyardCandidate(path.Type, path.Address, path.Port),
                        local.Address, local.Port, cancellationToken).ConfigureAwait(false);
                }

                Log($"negotiation answered (RESULT {consoleOffer.ReqId}, OFFER, ACCEPT peerSid={consoleOffer.Sid})");
            }

            var registrationRequest = new HalyardRegistrationRequest(
                request.ConsoleId, request.ConsoleHost, request.AccountId,
                Passcode: string.Empty, request.ClientDeviceId, request.Platform)
            {
                AccountSeed = recoveredSeed,
            };

            HalyardRegistrationResult result = await registration
                .RegisterAsync(registrationRequest, cancellationToken)
                .ConfigureAwait(false);
            Log(result.Succeeded ? "registered" : $"registration failed: {result.FailureReason}");
            return result;
        }
        finally
        {
            pushChannel.CustomData1Received -= OnCustomData1;
            pushChannel.SignalingReceived -= OnSignaling;
            pushChannel.ConsoleJoined -= OnJoined;

            // Leave the session we created, always — success or failure, and before the push channel goes down
            // so the leave is announced on a live connection like the vendor's is (its last frame is a
            // members:deleted for itself).
            //
            // This is not tidiness. Creating a session and walking away leaves the account holding a member in
            // a session nobody is in, one per attempt, and pairing is a thing users retry. A console that finds
            // the account already sitting in stale sessions is a plausible reason for it to refuse a new one —
            // which is exactly the failure being chased when this was found missing.
            if (joinedSessionId is not null)
            {
                await LeaveQuietlyAsync(joinedSessionId).ConfigureAwait(false);
            }

            await lifetime.CancelAsync().ConfigureAwait(false);
            await SafeAwaitAsync(pushLoop).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Our stream id, and the request id of our ACCEPT. Both are small constants because we open exactly one
    /// connection per pairing — the captured client uses 1 and 2 for its first, and only counts up when it
    /// negotiates a second stream for the media port.
    /// </summary>
    private const int OurStreamId = 1;

    private const int OurAcceptReqId = 2;

    /// <summary>
    /// Which of the console's candidates to accept: the one naming the address we already reached it on, so a
    /// same-network pairing keeps its traffic on the LAN rather than going out to the reflexive address and
    /// back. Falls back to the first offered.
    /// </summary>
    private static HalyardSignalingCandidate? PreferredCandidate(
        HalyardSignalingMessage offer, string consoleHost)
    {
        foreach (HalyardSignalingCandidate candidate in offer.Candidates)
        {
            if (string.Equals(candidate.Address, consoleHost, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return offer.Candidates.Count > 0 ? offer.Candidates[0] : null;
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    /// <summary>
    /// Leave the session without letting the attempt's outcome depend on it. Uses a fresh token rather than the
    /// caller's: teardown runs on the cancellation path too, and a leave that is skipped because the operation
    /// was cancelled is precisely the leak this exists to prevent.
    /// </summary>
    private async Task LeaveQuietlyAsync(string sessionId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _signaling.LeaveSessionAsync(sessionId, timeout.Token).ConfigureAwait(false);
            Log($"left session {sessionId}");
        }
        catch (Exception ex)
        {
            // Reported, not thrown: the pairing result is about the pairing, and a session PSN will expire on
            // its own must not turn a success into a failure.
            Log($"could not leave session {sessionId}: {ex.Message}");
        }
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The push loop is being torn down; its exit reason is not this method's concern.
        }
    }
}
