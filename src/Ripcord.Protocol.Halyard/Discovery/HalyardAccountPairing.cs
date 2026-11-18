using System.Net;
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
    (string Address, int Port)? LocalEndpoint = null,
    (string Address, int Port)? ReflexiveEndpoint = null);

/// <summary>
/// What the account route's transport needs, known only once the console has offered.
/// </summary>
/// <param name="ConsoleOffer">The console's OFFER, in full — its candidates and the id it names itself by.</param>
/// <param name="ConsoleHost">
/// The address the caller already had for the console, from LAN discovery. Preferred over a candidate when
/// the two agree, because it is the one we know is reachable from here.
/// </param>
/// <param name="LocalHashedId">Our own signaling id, as announced in our OFFER.</param>
/// <param name="ConsoleHashedId">The console's, from its OFFER.</param>
public sealed record HalyardAccountTransportContext(
    HalyardSignalingMessage ConsoleOffer,
    string ConsoleHost,
    ReadOnlyMemory<byte> LocalHashedId,
    ReadOnlyMemory<byte> ConsoleHashedId);

/// <summary>
/// What the shared rendezvous hands to whatever finishes it: the negotiated context, the seed if one was
/// required, the cloud session it all runs in, and a way to await the console's <em>next</em> OFFER.
///
/// <para>
/// The last of those is the media leg: the console offers a second connection, on its own sid, once the
/// control plane is up. Passed as a callback rather than a collection because it is awaited long after the
/// rendezvous returns -- the session asks for it while it is starting the stream.
/// </para>
/// </summary>
public sealed record RendezvousOutcome(
    HalyardAccountTransportContext Context,
    byte[] Seed,
    string SessionId,
    IAsyncDisposable SessionLifetime,
    Func<CancellationToken, Task<HalyardSignalingMessage?>> NextOffer);

/// <summary>Where the console's A/V leg is, once its second candidate exchange has been answered.</summary>
public sealed record HalyardMediaEndpoint(string Address, int Port, byte[] ConsoleHashedId);

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
    IAsyncDisposable? SessionLifetime = null,
    Func<int, (string Address, int Port)?, CancellationToken, Task<HalyardMediaEndpoint?>>? NegotiateMedia = null)
{
    public bool Succeeded => Channel is not null;

    public static HalyardAccountConnection Failed(string reason) => new(null, null, reason);
}

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

    /// <summary>
    /// How the registration transport's hello addresses the console — passed straight through to
    /// <c>HalyardDatagramControlOptions.HelloAddressing</c>. Here because the harness needs to vary it, and
    /// this is the options object the harness already builds.
    /// </summary>
    public HalyardControlAddressing HelloAddressing { get; init; } = HalyardControlAddressing.PortPair;

    /// <summary>
    /// How often to read the session back while waiting for the session service to list the console as a member,
    /// before messaging it. Two seconds is the vendor client's own rhythm in our captures (ps-rendezvous: its
    /// session reads at 0.1, 2.0 and 4.0 s).
    /// </summary>
    public TimeSpan MembershipPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long to wait for that before messaging the console anyway. The vendor's first message follows the
    /// console's OFFER by 3 to 9 s in every captured session.
    /// </summary>
    public TimeSpan MembershipTimeout { get; init; } = TimeSpan.FromSeconds(12);

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
/// The push channel is passed in before it is started (the caller built it over a real or fake WebSocket), which is
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
    /// Pair an account console: the seed arrives over the cloud and drives a <c>/sess/rgst</c>.
    /// <paramref name="pushChannel"/> is a channel that has not been started, over a WebSocket the caller owns;
    /// <paramref name="pushServer"/> and <paramref name="accessToken"/> drive its upgrade.
    /// </summary>
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
            finish: async (outcome, ct) =>
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
                    AccountSeed = outcome.Seed,
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
            finish: async (outcome, ct) =>
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
                    string? failure = await registerFirst(outcome.Context, outcome.Seed, ct).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        Log($"registration on the session's association failed: {failure}");
                        return HalyardAccountConnection.Failed(failure);
                    }

                    Log("registered on the session's association");
                }

                async Task<HalyardMediaEndpoint?> NegotiateMediaAsync(
                    int localPort, (string Address, int Port)? mediaReflexive, CancellationToken mediaToken)
                {
                    HalyardSignalingMessage? mediaOffer =
                        await outcome.NextOffer(mediaToken).ConfigureAwait(false);
                    if (mediaOffer is null)
                    {
                        Log("the console never offered a media connection");
                        return null;
                    }

                    // No fallback for the media leg: which port the console would serve A/V on at its known host is
                    // not something to guess, so an offer with no usable candidate is reported as no media path.
                    HalyardSignalingCandidate? path = HalyardConsoleCandidates.Choose(mediaOffer.Candidates);
                    if (path is null || request.LocalEndpoint is not { } local)
                    {
                        return null;
                    }

                    // Answered exactly as the control leg was: our own OFFER, then an ACCEPT naming the
                    // console's stream id. Our sid and reqId count on from the control connection's, as the
                    // captured client's do for its second stream. The sid is the half that matters: the
                    // console takes our id for this leg from the OFFER, not the ACCEPT, and offering stream 1
                    // again made its second connection collide with its first (preluded, then never served).
                    // All three candidates again: the A/V leg is a connection of its own on a port of its
                    // own. Offering only the local one left the media prelude unanswered off-network.
                    await _signaling.SendOfferAsync(
                        outcome.SessionId, request.AccountId, request.ConsoleDuid,
                        OurCandidates((local.Address, localPort), mediaReflexive),
                        mediaToken, request.LocalHashedId,
                        reqId: OurOfferReqId + 2, sid: OurStreamId + 1).ConfigureAwait(false);

                    await _signaling.SendAcceptAsync(
                        outcome.SessionId, request.AccountId, request.ConsoleDuid,
                        reqId: OurAcceptReqId + 2, sid: OurStreamId + 1, peerSid: mediaOffer.Sid,
                        new HalyardCandidate(path.Type, path.Address, path.Port),
                        local.Address, localPort, mediaToken).ConfigureAwait(false);

                    Log($"media connection negotiated (peerSid={mediaOffer.Sid}, console {path.Address}:{path.Port})");
                    return new HalyardMediaEndpoint(path.Address, path.Port, mediaOffer.LocalHashedId ?? []);
                }

                Log("control association ready for the session");
                return new HalyardAccountConnection(
                    channel, outcome.Context, null, outcome.SessionLifetime, NegotiateMediaAsync);
            },
            fail: HalyardAccountConnection.Failed,
            cancellationToken);
    }

    /// <summary>
    /// The account route's rendezvous, shared by pairing and connecting.
    ///
    /// <para>
    /// Everything up to "the control association is open" is identical for both: who offers first, where our
    /// Init sits relative to our OFFER and ACCEPT, which messages must be acknowledged. The two differ only in
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
        Func<RendezvousOutcome, CancellationToken, Task<T>> finish,
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

        // Counted so a timeout can tell two faults apart. A seed that arrived and did not open means our key
        // material, our context key or our encoding is wrong; a seed that never arrived means the console
        // never got as far as publishing one.
        int customDataSeen = 0;
        int customDataUnreadable = 0;

        void OnCustomData1(string customData1)
        {
            Interlocked.Increment(ref customDataSeen);

            try
            {
                seed.TrySetResult(HalyardAccountSeedDelivery.RecoverSeed(data1, data2, customData1, _contextKey));
            }
            catch (FormatException)
            {
                // Not a valid double-base64 customData1 for us — keep waiting.
                Interlocked.Increment(ref customDataUnreadable);
            }
            catch (ArgumentException)
            {
                // Wrong length after decode — keep waiting.
                Interlocked.Increment(ref customDataUnreadable);
            }
        }

        // The console's OFFER, which carries where it is and the id it will name itself by in the prelude.
        var offer = new TaskCompletionSource<HalyardSignalingMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Acknowledging the console takes the session id, which does not exist until further down; the
        // handler is registered before that, so it reads this rather than capturing a value that is still null.
        string? liveSessionId = null;
        var acked = new HashSet<(string Action, int ReqId)>();
        var ackLock = new object();

        // On the route that messages the console, our acks wait with everything else until the session service
        // lists it (WaitForConsoleMembershipAsync): sent the moment the console's OFFER arrived, the ack 404'd
        // "Member not joined session." (2026-10-10). The vendor's own ack for that OFFER comes seconds later.
        bool holdAcks = !request.LocalHashedId.IsEmpty;
        var heldAcks = new List<HalyardSignalingMessage>();

        // Ask the session service who is in the session, independently of the push channel.
        //
        // "The console never joined" is only what our push channel failed to hear. If the service lists it as
        // a member, the command worked and our subscription missed the announcement, which is our bug. If the
        // service lists only us, the cloud accepted the command and the console never acted on it, which is
        // a console or account problem.
        //
        // Diagnostic only: it never changes what the flow does, and it cannot fail the pairing.
        async Task LogMembershipAsync(string when)
        {
            if (_options.Log is null || liveSessionId is null)
            {
                return;
            }

            try
            {
                IReadOnlyList<HalyardCloudSession> sessions = await _signaling
                    .GetSessionAsync(liveSessionId, cancellationToken)
                    .ConfigureAwait(false);

                HalyardCloudSession? mine = sessions.FirstOrDefault(x => x.SessionId == liveSessionId);

                if (mine is null)
                {
                    Log($"session readback {when}: the service does not have our session at all");
                    return;
                }

                HalyardSessionMember[] members = mine.Members ?? [];

                // Identified by the duid we commanded, which is the only unambiguous test. Having a
                // deviceUniqueId at all is not one: we create the session with deviceUniqueId "me" and the
                // service resolves it, so this client has one too.
                bool consoleIsAMember = members.Any(
                    m => string.Equals(m.DeviceUniqueId, request.ConsoleDuid, StringComparison.Ordinal));

                Log($"session readback {when}: {members.Length} member(s); "
                    + $"the console {(consoleIsAMember ? "IS" : "is NOT")} among them");

                foreach (HalyardSessionMember member in members)
                {
                    bool isConsole = string.Equals(
                        member.DeviceUniqueId, request.ConsoleDuid, StringComparison.Ordinal);

                    Log($"  member platform={member.Platform} account={member.AccountId} "
                        + $"{(isConsole ? "<- the console we commanded" : "(not the commanded console)")}");
                }
            }
            catch (Exception ex)
            {
                Log($"session readback {when} failed: {ex.GetType().Name}");
            }
        }

        // Answer every signaling message the console sends with a RESULT carrying its reqId.
        //
        // Every one, not just the OFFER. Five mitmproxy captures of the vendor client (ps-rendezvous/cap71,
        // cap72, cap96, cap99, cap107) show it acking the console's ACCEPT too, in every session, within
        // ~1.5s. Without that ack the console sent its ACCEPT, waited, and then TERMINATE-d in every live run.
        //
        // Fired from the handler rather than awaited later in the flow: by the time the ACCEPT arrives the
        // flow has moved on to the transport, and the console gives up about a second later. Deduplicated on
        // (action, reqId) because the push channel delivers duplicates routinely (each OFFER arrives twice).
        void Ack(HalyardSignalingMessage message)
        {
            if (liveSessionId is not { } id)
            {
                return;
            }

            lock (ackLock)
            {
                if (holdAcks)
                {
                    if (!heldAcks.Exists(m => m.Action == message.Action && m.ReqId == message.ReqId))
                    {
                        heldAcks.Add(message);
                    }

                    return;
                }

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
                        Log($"could not ack the console's {message.Action} (reqId {message.ReqId}): {ex.Message}"
                            + ResponseBodyOf(ex));
                    }
                },
                CancellationToken.None);
        }

        void ReleaseAcks()
        {
            List<HalyardSignalingMessage> held;
            lock (ackLock)
            {
                holdAcks = false;
                held = [.. heldAcks];
                heldAcks.Clear();
            }

            foreach (HalyardSignalingMessage message in held)
            {
                Ack(message);
            }
        }

        // Whether the session service lists the console as a member: by the device id we commanded, or by any
        // member that is not a Remote Play client, which is how the console's join arrives on the push channel.
        async Task<bool> ConsoleIsListedAsync()
        {
            if (liveSessionId is not { } id)
            {
                return false;
            }

            IReadOnlyList<HalyardCloudSession> sessions =
                await _signaling.GetSessionAsync(id, cancellationToken).ConfigureAwait(false);
            HalyardSessionMember[] members = sessions.FirstOrDefault(x => x.SessionId == id)?.Members ?? [];
            return members.Any(m =>
                string.Equals(m.DeviceUniqueId, request.ConsoleDuid, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(m.Platform) && m.Platform != "REMOTE_PLAY"));
        }

        // **Message the console only once the session service lists it.** The push channel's join, and the
        // console's own OFFER after it, come before the service will take a message for the console: sent then,
        // our OFFER 404'd "Member not joined session." in every run from a hotspot with the console woken from
        // rest (2026-10-10), and the route ended. In our captures the vendor client reads its session back on a
        // two-second rhythm and sends its first message 3 to 9 s after the console's OFFER, never a 404 in about
        // 150 (captures folder, away-2026-10-10/NOTES.md). That it waits for exactly this read is inferred from
        // the timing and the read's size, [X]. Bounded: past MembershipTimeout it messages the console anyway.
        async Task WaitForConsoleMembershipAsync()
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            for (int read = 1; ; read++)
            {
                try
                {
                    if (await ConsoleIsListedAsync().ConfigureAwait(false))
                    {
                        Log($"the session service lists the console (read {read}, {waited.Elapsed.TotalSeconds:0.0}s "
                            + "after its OFFER)");
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"session read {read} failed: {ex.GetType().Name}");
                }

                if (waited.Elapsed + _options.MembershipPollInterval > _options.MembershipTimeout)
                {
                    Log($"the session service has not listed the console after {waited.Elapsed.TotalSeconds:0.0}s "
                        + "(" + read + " reads); messaging it anyway");
                    return;
                }

                await Task.Delay(_options.MembershipPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        // The console offers the media connection separately, on its own sid, once the control plane is up --
        // so a second OFFER is not a duplicate to be ignored but the A/V leg arriving.
        var mediaOffers = System.Threading.Channels.Channel.CreateUnbounded<HalyardSignalingMessage>();
        bool sawFirstOffer = false;

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
                if (offer.TrySetResult(message))
                {
                    sawFirstOffer = true;
                    return;
                }

                // A repeat of the first is just the push channel delivering it twice; a new sid is the media
                // connection being offered.
                if (sawFirstOffer && message.Sid != offer.Task.Result.Sid)
                {
                    mediaOffers.Writer.TryWrite(message);
                }
            }
        }

        // Which session each push frame is about. Every frame names one (body.data.sessionId, in our captures of
        // the vendor client), and a join or an OFFER from another of the account's sessions must not count as
        // ours: from a hotspot the console "joined" and offered, yet the service never listed it in our session
        // and refused every message to it as not a member (2026-10-10). Diagnostic only: says whether the frame
        // is ours, never the id itself.
        void OnFrame(string frame)
        {
            if (_options.Log is null)
            {
                return;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(frame);
                System.Text.Json.JsonElement root = doc.RootElement;
                string type = root.TryGetProperty("dataType", out System.Text.Json.JsonElement dt)
                    && dt.GetString() is { } dataType
                        ? string.Join(':', dataType.Split(':').TakeLast(2))
                        : "?";
                string? frameSession =
                    root.TryGetProperty("body", out System.Text.Json.JsonElement fb)
                    && fb.TryGetProperty("data", out System.Text.Json.JsonElement fd)
                    && fd.TryGetProperty("sessionId", out System.Text.Json.JsonElement fs)
                        ? fs.GetString()
                        : null;

                string whose = frameSession is null ? "no session id"
                    : liveSessionId is null ? "before ours exists"
                    : string.Equals(frameSession, liveSessionId, StringComparison.Ordinal) ? "OUR session"
                    : "ANOTHER session";
                Log($"push frame {type}: {whose}");
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON: a keep-alive. Nothing to say.
            }
        }

        // The console joining is the earliest moment we may message it, and the moment before it starts its
        // own control association. Everything the client wants to get in early hangs off this.
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnJoined() => joined.TrySetResult();

        pushChannel.CustomData1Received += OnCustomData1;
        pushChannel.SignalingReceived += OnSignaling;
        pushChannel.ConsoleJoined += OnJoined;
        pushChannel.FrameReceived += OnFrame;

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

            // From here, only this session's frames reach the handlers above.
            pushChannel.SessionId = sessionId;
            Log($"session created: {sessionId}");

            await _signaling.SendConnectCommandAsync(
                request.ConsoleDuid, request.AccountId, sessionId, request.ClientType,
                (Convert.ToBase64String(data1), Convert.ToBase64String(data2), string.Empty), cancellationToken)
                .ConfigureAwait(false);
            Log("connect command sent (data1/data2)");

            // The console joining is the first moment it can be messaged at all — a sessionMessage only
            // reaches a member. It is NOT the moment to offer: on this route the console initiates the
            // signaling (its OFFER arrives unprompted, with its own reqId) and we are the responder. Offering
            // first, tried live, got a TERMINATE and no control association at all.
            // Three states, not two: joined, waited-and-it-never-came, and never-waited. The join wait only
            // runs on the route that needs it, so null keeps a failure message from claiming the console
            // never turned up when nothing looked.
            bool? consoleJoined = null;

            if (!request.LocalHashedId.IsEmpty)
            {
                try
                {
                    await joined.Task.WaitAsync(_options.OfferTimeout, cancellationToken).ConfigureAwait(false);
                    consoleJoined = true;
                    Log("console joined the session");
                }
                catch (TimeoutException)
                {
                    consoleJoined = false;
                    Log("the console never joined the session");
                    await LogMembershipAsync("after the join wait").ConfigureAwait(false);
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
                    int seen = Volatile.Read(ref customDataSeen);
                    int unreadable = Volatile.Read(ref customDataUnreadable);

                    Log($"seed wait timed out after {_options.SeedTimeout.TotalSeconds:0}s "
                        + $"(customData1 frames seen: {seen}, unreadable: {unreadable})");

                    await LogMembershipAsync("after the seed wait").ConfigureAwait(false);

                    // Ordered by how far upstream the fault is: a console that never joined cannot have
                    // published anything, so blaming the seed there would hide the cause.
                    //
                    // A console signed out of the account service is one such cause, and an invisible one.
                    // Five runs (2026-09-24) against a console signed out of PSN after the account's security
                    // options changed: it answered LAN discovery under its own name throughout, the console
                    // list still reported remote play enabled and both standby wake modes, and every command
                    // was accepted with a commandId in ~150 ms. Signing in on the console fixed it from rest
                    // mode, first attempt, no restart.
                    //
                    // Nothing in the console list reports it — the fields are name, language,
                    // wakeupEnabledPowerModes, enabledFeatures, updatedDateTime, duid, platform, with no
                    // presence or online flag — so a client cannot check for it and can only name it among
                    // the things to look at.
                    return fail(
                        consoleJoined == false && seen == 0
                            ? "The console never joined the session, so it never got as far as publishing a "
                              + "registration seed. It has to be awake or able to be woken over the internet "
                              + "from rest mode, reachable by the account service, and signed in to "
                              + "PlayStation Network — a console that is signed out still answers on your "
                              + "network and still looks available here."
                        : unreadable > 0
                            ? $"The console published a registration seed ({unreadable} of {seen} customData1 "
                              + "frames) but none of them could be decrypted with this session's key material."
                        : consoleJoined == true
                            ? "The console joined the session but did not publish the registration seed "
                              + "(customData1) in time."
                        : "The console did not publish the registration seed (customData1) in time.");
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

            if (!request.LocalHashedId.IsEmpty)
            {
                await WaitForConsoleMembershipAsync().ConfigureAwait(false);
                ReleaseAcks();
            }

            var context = new HalyardAccountTransportContext(
                consoleOffer, request.ConsoleHost, request.LocalHashedId, consoleOffer.LocalHashedId!);


            // Announce ourselves — now, and not a moment earlier. A sessionMessage may only be sent to a
            // member, so this 404s until the console has joined. (Observed as a live 404 when this was sent
            // right after the command.) The console's OFFER is not proof enough of that: from rest it 404'd
            // after the OFFER too, which is what the membership wait above is for. It must still precede the
            // prelude: the console has to have seen the id and port we are about to speak from.
            if (!request.LocalHashedId.IsEmpty)
            {
                // The negotiation is a real offer/accept, not two independent advertisements — the capture
                // shows every message acknowledged by a RESULT carrying the sender's reqId, and the client
                // answering the console's OFFER with an ACCEPT that names the console's stream id. Stopping
                // after our own OFFER leaves the console waiting, and it never opens its side (observed live
                // as five unanswered preludes).
                // The local candidate alone is enough only when the console is on this network; anywhere
                // else it is a private address the console cannot reach, observed as our Init leaving every
                // five seconds with silence back.
                IReadOnlyList<HalyardCandidate> ours =
                    OurCandidates(request.LocalEndpoint, request.ReflexiveEndpoint);

                await _signaling.SendOfferAsync(
                    sessionId, request.AccountId, request.ConsoleDuid, ours, cancellationToken,
                    request.LocalHashedId).ConfigureAwait(false);

                // Our Init goes out HERE: after our OFFER, before our ACCEPT.
                //
                // The side that opens the association is the side that may open connections on it. Both
                // neighbouring orderings are falsified against hardware. Before the OFFER: the console has
                // not seen our localHashedId yet, discards the Init, and opens an association of its own
                // (shown by the tag pair: a console that accepts an Init answers with the sender's own pair
                // with its halves exchanged, and against an early Init it always announced a pair of its own
                // invention). After the whole exchange: our three signaling POSTs take about two seconds, and
                // the console initiates within ~200ms of our OFFER, so it is already retrying.
                //
                // This position is where the captured vendor client's Init actually sits, measured from its
                // TLS streams: one POST at t+18.85, its Init at t+20.33, two more POSTs at t+20.34. Its
                // console then answered by adopting its tag pair 148ms later and never raced it. **[X]** why
                // the console tolerates the wait there and not here is unexplained.
                try
                {
                    await openAssociation(context, cancellationToken).ConfigureAwait(false);
                    Log("control association opened (after our OFFER, before our ACCEPT)");
                }
                catch (Exception ex)
                {
                    Log($"could not open the control association: {ex.Message}");
                }

                // The same decision the transport made (HalyardConsoleCandidates.Resolve), fallback included.
                HalyardSignalingCandidate? path = HalyardConsoleCandidates.Resolve(
                    consoleOffer.Candidates, request.ConsoleHost, HalyardDatagramRegistrationTransport.Port)?.Named;
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

            async Task<HalyardSignalingMessage?> NextOfferAsync(CancellationToken offerToken)
            {
                try
                {
                    return await mediaOffers.Reader.ReadAsync(offerToken).AsTask()
                        .WaitAsync(_options.OfferTimeout, offerToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return null;
                }
            }

            var outcome = new RendezvousOutcome(
                context, recoveredSeed, sessionId, sessionLifetime, NextOfferAsync);
            T finished = await finish(outcome, cancellationToken).ConfigureAwait(false);

            // Only from here is the caller holding it: a failure before this point still tears down below.
            handedOff = keepSessionOpen;
            return finished;
        }
        catch (Exception ex) when (LogFailure(ex))
        {
            // Never reached: LogFailure records why the route ended and declines to catch.
            throw;
        }
        finally
        {
            // Only when we still own them. A handed-off connect must keep ACKING: the console goes on sending
            // signaling for the life of the session, and one it does not get a RESULT for is one it gives up
            // on -- observed live as an ACCEPT, then a TERMINATE, then a control connection that never opened.
            // The lifetime object unsubscribes instead.
            pushChannel.FrameReceived -= OnFrame;

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
            // Not leaving would hold a member in an empty session, one per attempt, and pairing is retried. An
            // account already sitting in stale sessions is a plausible reason for a console to refuse a new one.
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

    /// <summary>
    /// Our stream id for the control connection, and below it the request ids of our OFFER and ACCEPT. The
    /// captured client uses 1 and 2 for its first connection and counts up when it negotiates a second stream
    /// for the media port; the media leg here does the same (stream id + 1, request ids + 2).
    /// </summary>
    private const int OurStreamId = 1;

    private const int OurOfferReqId = 1;

    private const int OurAcceptReqId = 2;

    /// <summary>
    /// The candidates we offer for one connection, in the order and with the labels the captured client uses.
    ///
    /// <para>
    /// Three, not two:
    /// </para>
    /// <list type="bullet">
    /// <item><c>STUN</c> — the public address <b>and the port the NAT actually assigned</b>, as a STUN server
    /// reported it. This is the mapping that demonstrably exists.</item>
    /// <item><c>STATIC</c> — the same public address with <b>our own local port</b>. A guess, and a good one:
    /// many NATs preserve the port, and where they do this is reachable even if the STUN mapping has since
    /// expired or was taken for a different destination.</item>
    /// <item><c>LOCAL</c> — our address on this network, which is the only one that works when the peer turns
    /// out to be on it.</item>
    /// </list>
    ///
    /// <para>
    /// The guess matters behind a NAT that maps per destination: the mapping a STUN server sees is then not
    /// the one the console would get, and the STUN candidate alone leaves nothing to fall back on.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<HalyardCandidate> OurCandidates(
        (string Address, int Port)? local, (string Address, int Port)? reflexive)
    {
        var candidates = new List<HalyardCandidate>();

        if (reflexive is { } seen)
        {
            candidates.Add(new HalyardCandidate("STUN", seen.Address, seen.Port));

            // Only when it differs: a port-preserving NAT makes the guess identical to the mapping, and two
            // byte-identical candidates say nothing the first did not.
            if (local is { } mine && seen.Port != mine.Port)
            {
                candidates.Add(new HalyardCandidate("STATIC", seen.Address, mine.Port));
            }
        }

        if (local is { } advertised)
        {
            candidates.Add(new HalyardCandidate("LOCAL", advertised.Address, advertised.Port));
        }

        return candidates;
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    /// <summary>
    /// Records why the route ended, then lets the exception go on unchanged (an exception filter that returns false).
    /// A first try from a hotspot ended half a second after the console's OFFER with nothing in the trace to say why
    /// (2026-10-10).
    /// </summary>
    private bool LogFailure(Exception ex)
    {
        Log($"the route ended: {ex.GetType().Name}: {ex.Message}" + ResponseBodyOf(ex));
        return false;
    }

    private static string ResponseBodyOf(Exception ex)
        => ex is HalyardCloudException { ResponseBody: { Length: > 0 } body }
            ? $" (body: {(body.Length > 300 ? body[..300] : body)})"
            : string.Empty;

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
