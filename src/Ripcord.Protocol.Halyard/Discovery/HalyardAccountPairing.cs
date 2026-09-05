using Ripcord.Cloud.Halyard;
using Ripcord.Cloud.Halyard.Rendezvous;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Transport;
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
/// <summary>
/// A live account-route association, ready for the session control plane, or the reason there is not one.
///
/// <para>
/// <see cref="Channel"/> is the association the whole session runs on -- <c>/sess/init</c>, <c>/sess/ctrl</c>
/// and the control frames after them. The caller owns it: disposing it ends the session.
/// </para>
/// </summary>
public sealed record HalyardAccountConnection(
    HalyardDatagramControlChannel? Channel,
    HalyardAccountTransportContext? Context,
    string? FailureReason,
    IAsyncDisposable? SessionLifetime = null)
{
    public bool Succeeded => Channel is not null;

    public static HalyardAccountConnection Failed(string reason) => new(null, null, reason);
}

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

    /// <summary>
    /// How the registration transport's hello addresses the console — passed straight through to
    /// <c>HalyardDatagramControlOptions.HelloAddressing</c>. Here because the harness needs to vary it, and
    /// this is the options object the harness already builds.
    /// </summary>
    public HalyardControlAddressing HelloAddressing { get; init; } = HalyardControlAddressing.PortPair;

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
    /// <summary>Pair with the console: the seed arrives over the cloud and drives a <c>/sess/rgst</c>.</summary>
    public Task<HalyardRegistrationResult> PairAsync(
        HalyardAccountPairingRequest request,
        HalyardPushChannel pushChannel,
        HalyardPushServerInfo pushServer,
        string accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        IHalyardRegistration? registration = null;
        return RunAsync(
            request, pushChannel, pushServer, accessToken, requireSeed: true, keepSessionOpen: false,
            openAssociation: (context, ct) =>
            {
                registration = _registration(context);
                return registration.PrepareAsync(ct);
            },
            finish: async (_, seed, _, ct) =>
            {
                if (registration is null)
                {
                    return new HalyardRegistrationResult(false,
                        "The control association was never opened, so there is nothing to register over.", null);
                }

                var registrationRequest = new HalyardRegistrationRequest(
                    request.ConsoleId, request.ConsoleHost, request.AccountId,
                    Passcode: string.Empty, request.ClientDeviceId, request.Platform)
                {
                    AccountSeed = seed,
                };

                HalyardRegistrationResult result = await registration
                    .RegisterAsync(registrationRequest, ct)
                    .ConfigureAwait(false);
                Log(result.Succeeded ? "registered" : $"registration failed: {result.FailureReason}");
                return result;
            },
            fail: reason => new HalyardRegistrationResult(false, reason, null),
            cancellationToken);
    }

    /// <summary>
    /// Reach an already-paired console over the account route, and hand back the open association.
    ///
    /// <para>
    /// The same rendezvous as pairing, minus the seed: the console serves <c>/sess/init</c> and
    /// <c>/sess/ctrl</c> on the association that would have carried <c>/sess/rgst</c>, so what a caller
    /// wants back is the live association rather than a pairing record. The caller owns it from here, and
    /// disposing it ends the session.
    /// </para>
    /// </summary>
    /// <param name="transportFactory">
    /// Builds the association for the console the OFFER named. Injected for the same reason the
    /// registration is: the endpoint is not known until the console has offered.
    /// </param>
    public Task<HalyardAccountConnection> ConnectAsync(
        HalyardAccountPairingRequest request,
        HalyardPushChannel pushChannel,
        HalyardPushServerInfo pushServer,
        string accessToken,
        Func<HalyardAccountTransportContext, HalyardDatagramRegistrationTransport> transportFactory,
        Func<HalyardAccountTransportContext, byte[], CancellationToken, Task<string?>>? registerFirst,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transportFactory);

        HalyardDatagramRegistrationTransport? transport = null;
        return RunAsync(
            // The seed is required exactly when we intend to register. The console publishes a customData1 on
            // every account-route session, connects included, which is what suggests registration is part of
            // each one rather than a one-time pairing -- and both captures show rgst, init and ctrl on a
            // single association.
            request, pushChannel, pushServer, accessToken,
            requireSeed: registerFirst is not null, keepSessionOpen: true,
            openAssociation: (context, ct) =>
            {
                transport = transportFactory(context);
                return transport.PrepareAsync(ct);
            },
            finish: async (context, seed, sessionLifetime, ct) =>
            {
                HalyardDatagramControlChannel? channel = transport?.Channel;
                if (channel is null)
                {
                    Log("no control association to connect over");
                    return HalyardAccountConnection.Failed(
                        "The control association was never opened, so there is nothing to connect over.");
                }

                if (registerFirst is not null)
                {
                    string? failure = await registerFirst(context, seed, ct).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        Log($"registration on the session's association failed: {failure}");
                        return HalyardAccountConnection.Failed(failure);
                    }

                    Log("registered on the session's association");
                }

                Log("control association ready for the session");
                return new HalyardAccountConnection(channel, context, null, sessionLifetime);
            },
            fail: HalyardAccountConnection.Failed,
            cancellationToken);
    }

    /// <summary>
    /// The account route's rendezvous, shared by pairing and connecting.
    ///
    /// <para>
    /// Everything up to "the control association is open" is identical for both, and the sequence is
    /// unforgiving enough -- who offers first, where our Init sits relative to our OFFER and ACCEPT, which
    /// messages must be acknowledged -- that a second copy of it would drift. So the two differ only in
    /// <paramref name="openAssociation"/> (what to build the association with), <paramref name="finish"/>
    /// (what to do once it is open) and whether the registration seed is required.
    /// </para>
    /// </summary>
    /// <param name="openAssociation">
    /// Opens the association. Called at one exact point -- after our OFFER, before our ACCEPT -- because that
    /// is where the captured client's Init sits and both neighbouring orderings are falsified against
    /// hardware.
    /// </param>
    private async Task<T> RunAsync<T>(
        HalyardAccountPairingRequest request,
        HalyardPushChannel pushChannel,
        HalyardPushServerInfo pushServer,
        string accessToken,
        bool requireSeed,
        bool keepSessionOpen,
        Func<HalyardAccountTransportContext, CancellationToken, Task> openAssociation,
        Func<HalyardAccountTransportContext, byte[], IAsyncDisposable, CancellationToken, Task<T>> finish,
        Func<string, T> fail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pushChannel);
        ArgumentNullException.ThrowIfNull(pushServer);

        (byte[] data1, byte[] data2) = HalyardAccountSeedDelivery.GenerateEphemeralKeyMaterial();

        // Set as soon as the session exists, so the teardown below can leave whatever we joined — including on
        // the failure paths, which is where it matters.
        string? joinedSessionId = null;

        // Not a `using`: when a connect takes ownership this outlives the call, and the lifetime object
        // disposes it instead.
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

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

        // Acknowledging the console takes the session id, which does not exist until further down; the
        // handler is registered before that, so it reads this rather than capturing a value that is still null.
        string? liveSessionId = null;
        var acked = new HashSet<(string Action, int ReqId)>();
        var ackLock = new object();

        // Answer every signaling message the console sends with a RESULT carrying its reqId.
        //
        // **This is the thing that was missing.** Ripcord acked the console's OFFER and nothing else. Five
        // mitmproxy captures of the vendor client (ps-rendezvous/cap71, cap72, cap96, cap99, cap107) show it
        // acking the console's ACCEPT too, in every session, within ~1.5s. Ours never did — and in every live
        // run the console sent its ACCEPT, waited, and then TERMINATE-d.
        //
        // Fired from the handler rather than awaited at some later point in the flow, because by the time the
        // ACCEPT arrives the flow has moved on to the transport, and the console gives up about a second
        // later. Deduplicated on (action, reqId) because the push channel delivers duplicates routinely — we
        // see each OFFER twice.
        void Ack(HalyardSignalingMessage message)
        {
            if (liveSessionId is not { } id)
            {
                return;
            }

            lock (ackLock)
            {
                if (!acked.Add((message.Action, message.ReqId)))
                {
                    return;
                }
            }

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await _signaling
                            .SendResultAsync(id, request.AccountId, request.ConsoleDuid, message.ReqId, cancellationToken)
                            .ConfigureAwait(false);
                        Log($"acked the console's {message.Action} (reqId {message.ReqId})");
                    }
                    catch (Exception ex)
                    {
                        Log($"could not ack the console's {message.Action} (reqId {message.ReqId}): {ex.Message}");
                    }
                },
                CancellationToken.None);
        }

        void OnSignaling(HalyardSignalingMessage message)
        {
            if (message.ExpectsResult)
            {
                Ack(message);
            }

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

        // Set once the caller has taken ownership of the session and the push loop; until then the teardown
        // below owns them, including on every failure path.
        bool handedOff = false;

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
            liveSessionId = sessionId;
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

            // Only registration needs the seed. A connect still sends data1/data2 in the command (the console
            // publishes a customData1 either way, and the vendor's client sends them on both routes), but it
            // has a pairing record already and must not stall waiting for a value it will not use.
            byte[] recoveredSeed = [];
            if (requireSeed)
            {
                try
                {
                    recoveredSeed = await seed.Task
                        .WaitAsync(_options.SeedTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return fail("The console did not publish the registration seed (customData1) in time.");
                }

                Log("registration seed recovered from customData1");
            }

            HalyardSignalingMessage consoleOffer;
            try
            {
                consoleOffer = await offer.Task.WaitAsync(_options.OfferTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return fail("The console never offered its candidates, so there is no address to reach it at.");
            }

            Log($"console OFFER received ({consoleOffer.Candidates.Count} candidates)");

            var context = new HalyardAccountTransportContext(
                consoleOffer, request.ConsoleHost, request.LocalHashedId, consoleOffer.LocalHashedId!);


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
                IReadOnlyList<HalyardCandidate> ours = request.LocalEndpoint is { } advertised
                    ? [new HalyardCandidate("LOCAL", advertised.Address, advertised.Port)]
                    : [];

                await _signaling.SendOfferAsync(
                    sessionId, request.AccountId, request.ConsoleDuid, ours, cancellationToken,
                    request.LocalHashedId).ConfigureAwait(false);

                // Our Init goes out HERE: after our OFFER, before our ACCEPT.
                //
                // The side that opens the association is the side that may open connections on it, so this
                // race matters. Two orderings are now falsified against hardware. Sending it before the OFFER
                // does not work: the console has not seen our localHashedId yet, discards the Init, and then
                // opens an association of its own — proven by the tag pair, since a console that accepts an
                // Init answers with the sender's own pair with its halves exchanged, and against an early Init
                // it always announced a pair of its own invention. Sending it after the whole exchange is
                // worse: our three signaling POSTs take about two seconds, and the console initiates within
                // ~200ms of our OFFER, so it is already retrying by the time we speak.
                //
                // This position is where the captured vendor client's Init actually sits, measured from its
                // TLS streams: one POST at t+18.85, its Init at t+20.33, two more POSTs at t+20.34. Its
                // console then answered by adopting its tag pair 148ms later and never raced it. **[X]** why
                // the console tolerates the wait there and not here is still unexplained.
                try
                {
                    await openAssociation(context, cancellationToken).ConfigureAwait(false);
                    Log("control association opened (after our OFFER, before our ACCEPT)");
                }
                catch (Exception ex)
                {
                    Log($"could not open the control association: {ex.Message}");
                }

                HalyardSignalingCandidate? path = PreferredCandidate(consoleOffer, request.ConsoleHost);
                if (path is not null && request.LocalEndpoint is { } local)
                {
                    await _signaling.SendAcceptAsync(
                        sessionId, request.AccountId, request.ConsoleDuid,
                        reqId: OurAcceptReqId, sid: OurStreamId, peerSid: consoleOffer.Sid,
                        new HalyardCandidate(path.Type, path.Address, path.Port),
                        local.Address, local.Port, cancellationToken).ConfigureAwait(false);
                }

                Log($"negotiation answered (OFFER, ACCEPT peerSid={consoleOffer.Sid}); "
                    + "every console message is acked as it arrives");
            }

            // Handed to the caller so a connect can hold the session open for the life of the stream. Pairing
            // ignores it and the finally below does the same work.
            var sessionLifetime = new AccountSessionLifetime(
                this, joinedSessionId, lifetime, pushLoop, pushChannel, OnCustomData1, OnSignaling, OnJoined);
            T finished = await finish(context, recoveredSeed, sessionLifetime, cancellationToken)
                .ConfigureAwait(false);

            // Only from here is the caller holding it: a failure before this point still tears down below.
            handedOff = keepSessionOpen;
            return finished;
        }
        finally
        {
            // Only when we still own them. A handed-off connect must keep ACKING: the console goes on sending
            // signaling for the life of the session, and one it does not get a RESULT for is one it gives up
            // on -- observed live as an ACCEPT, then a TERMINATE, then a control connection that never opened.
            // The lifetime object unsubscribes instead.
            if (!handedOff)
            {
                pushChannel.CustomData1Received -= OnCustomData1;
                pushChannel.SignalingReceived -= OnSignaling;
                pushChannel.ConsoleJoined -= OnJoined;
            }

            // Leave the session we created, always — success or failure, and before the push channel goes down
            // so the leave is announced on a live connection like the vendor's is (its last frame is a
            // members:deleted for itself).
            //
            // This is not tidiness. Creating a session and walking away leaves the account holding a member in
            // a session nobody is in, one per attempt, and pairing is a thing users retry. A console that finds
            // the account already sitting in stale sessions is a plausible reason for it to refuse a new one —
            // which is exactly the failure being chased when this was found missing.
            if (!handedOff)
            {
                if (joinedSessionId is not null)
                {
                    await LeaveQuietlyAsync(joinedSessionId).ConfigureAwait(false);
                }

                await lifetime.CancelAsync().ConfigureAwait(false);
                await SafeAwaitAsync(pushLoop).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Our stream id, and the request id of our ACCEPT. Both are small constants because we open exactly one
    /// connection per pairing — the captured client uses 1 and 2 for its first, and only counts up when it
    /// negotiates a second stream for the media port.
    /// </summary>
    /// <summary>
    /// The cloud half of a connect: the session membership and the push loop, kept alive for as long as the
    /// stream runs.
    ///
    /// <para>
    /// A connect cannot do what pairing does and leave as soon as the association is open. Leaving ends the
    /// session the console joined, and the console tears the association down with it -- observed live as a
    /// control connection that timed out immediately after a rendezvous that had otherwise gone perfectly.
    /// </para>
    /// </summary>
    private sealed class AccountSessionLifetime(
        HalyardAccountPairing owner,
        string? sessionId,
        CancellationTokenSource lifetime,
        Task pushLoop,
        HalyardPushChannel pushChannel,
        Action<string> onCustomData1,
        Action<HalyardSignalingMessage> onSignaling,
        Action onJoined) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            pushChannel.CustomData1Received -= onCustomData1;
            pushChannel.SignalingReceived -= onSignaling;
            pushChannel.ConsoleJoined -= onJoined;

            // Leave before the push channel goes down, so the leave is announced on a live connection --
            // the vendor's last frame is a members:deleted for itself.
            if (sessionId is not null)
            {
                await owner.LeaveQuietlyAsync(sessionId).ConfigureAwait(false);
            }

            await lifetime.CancelAsync().ConfigureAwait(false);
            await SafeAwaitAsync(pushLoop).ConfigureAwait(false);
            lifetime.Dispose();
        }
    }

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
