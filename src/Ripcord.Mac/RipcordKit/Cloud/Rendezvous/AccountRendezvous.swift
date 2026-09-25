// The account route's rendezvous: account ("web"/no-PIN) pairing, and the internet-play connect that
// shares its sequence. Ported from HalyardAccountPairing.cs (PairAsync, ConnectAsync and the RunAsync they
// share), with the UDP steps behind AccountRouteTransport.
//
// THE TIMELINE, which is the whole point of this type and is kept exactly:
//
//    1. fresh data1/data2                                              transport.makeSeedKeyMaterial
//    2. subscribe to the push channel, then start its loop, BEFORE triggering the console, so a seed or
//       an OFFER published the instant it joins is not missed
//    3. wait for the push upgrade (the session binds to the live connection when it is created)
//    4. create the session; send the connect command carrying data1/data2
//    5. wait for the console to join                                   only when we have a hashed id
//    6. wait for customData1 and recover the seed                      only when registering
//    7. wait for the console's OFFER (it initiates; offering first was tried live and the console
//       TERMINATE-d)
//    8. POST our OFFER
//    9. OUR DATAGRAM INIT                                              transport.openAssociation
//   10. POST our ACCEPT naming the console's sid
//   11. finish: register (pairing), or hand the session over (connect)
//   12. leave the session and stop the push loop (pairing, or any failure)
//
// and, throughout, every OFFER and ACCEPT the console sends is answered with a RESULT the moment it arrives,
// deduplicated on (action, reqId), from a task of its own so the flow never waits on it.

import Foundation
import Synchronization

/// What identifies the console and account. The .NET HalyardAccountPairingRequest.
public struct AccountRendezvousRequest: Sendable, Equatable {
    public var consoleID: String
    /// The console's reachable host, for the registration and as the preferred path.
    public var consoleHost: String
    /// The console's device unique id, from the cloud console list.
    public var consoleDUID: String
    public var accountID: String
    /// This Mac's 16-byte device id.
    public var clientDeviceID: [UInt8]
    public var family: ConsoleFamily
    /// The wire client-type tag. `Windows`, as .NET: whether another value is accepted is [X].
    public var clientType: String
    /// Our 20-byte signaling id. Empty skips the join wait and the whole OFFER/Init/ACCEPT exchange, which
    /// is .NET's TCP fallback and which an account console refuses.
    public var localHashedID: [UInt8]
    /// Where our control transport speaks from: our `LOCAL` candidate, and the `mappedAddr` of our ACCEPT.
    /// The vendor's OFFER lists the very port its 9303 traffic then comes from.
    public var localEndpoint: UDPEndpoint?
    /// That socket's STUN mapping, if one was found.
    public var reflexiveEndpoint: UDPEndpoint?

    public init(consoleID: String, consoleHost: String, consoleDUID: String, accountID: String,
                clientDeviceID: [UInt8], family: ConsoleFamily = .ps5, clientType: String = "Windows",
                localHashedID: [UInt8] = [], localEndpoint: UDPEndpoint? = nil, reflexiveEndpoint: UDPEndpoint? = nil) {
        self.consoleID = consoleID
        self.consoleHost = consoleHost
        self.consoleDUID = consoleDUID
        self.accountID = accountID
        self.clientDeviceID = clientDeviceID
        self.family = family
        self.clientType = clientType
        self.localHashedID = localHashedID
        self.localEndpoint = localEndpoint
        self.reflexiveEndpoint = reflexiveEndpoint
    }
}

public struct AccountRendezvousOptions: Sendable {
    /// How long to wait for customData1 after the command.
    public var seedTimeout: Duration = .seconds(30)
    /// How long to wait for the console to join, for its OFFER, and for each later OFFER. Separate from the
    /// seed wait because they are separate events and either can come first.
    public var offerTimeout: Duration = .seconds(30)
    /// Progress lines for a harness. Also switches on the session readbacks, which exist only to be logged.
    public var log: (@Sendable (String) -> Void)?

    public init(seedTimeout: Duration = .seconds(30), offerTimeout: Duration = .seconds(30),
                log: (@Sendable (String) -> Void)? = nil) {
        self.seedTimeout = seedTimeout
        self.offerTimeout = offerTimeout
        self.log = log
    }
}

/// Where the console's A/V leg is, once its second candidate exchange has been answered.
public struct MediaEndpoint: Sendable, Equatable {
    public var address: String
    public var port: Int
    public var consoleHashedID: [UInt8]
}

/// A live account-route session, handed to the caller of `connect`. Closing it is the disconnect.
public struct AccountConnection<Registration: Sendable>: Sendable {
    public let context: AccountTransportContext
    public let sessionID: String
    /// What `registerFirst` produced, when it was asked for.
    public let registration: Registration?
    /// The session membership and the push loop, kept alive for the life of the stream.
    public let lifetime: AccountSessionLifetime
    let media: @Sendable (Int, UDPEndpoint?) async throws -> MediaEndpoint?

    /// Answers the console's second OFFER, the A/V leg, exactly as the control leg was answered: our own
    /// OFFER (sid 2, reqId 3, all three candidates again, since the media port needs its own discovery),
    /// then an ACCEPT (reqId 4) naming the console's new sid. Nil when the console never offered one.
    ///
    /// - Parameters:
    ///   - localPort: the media socket's local port.
    ///   - reflexive: the media socket's own STUN mapping.
    public func negotiateMedia(localPort: Int, reflexive: UDPEndpoint?) async throws -> MediaEndpoint? {
        try await media(localPort, reflexive)
    }
}

/// The cloud half of a connect, held open for as long as the stream runs.
///
/// A connect cannot leave the session as pairing does: leaving ends the session the console joined, and the
/// console tears the association down with it (observed live as a control connection that timed out right
/// after a rendezvous that had otherwise gone perfectly). And it must keep acknowledging: the console goes on
/// signaling for the life of the session, and gives up on a message it gets no RESULT for.
public final class AccountSessionLifetime: Sendable {
    private let closeOnce: Mutex<(@Sendable () async -> Void)?>

    init(close: @escaping @Sendable () async -> Void) { closeOnce = Mutex(close) }

    /// Unsubscribes, leaves the session (announced on the still-live push connection, as the vendor's last
    /// frame is a members:deleted for itself), then stops the push loop. Idempotent.
    public func close() async {
        let close = closeOnce.withLock { value in
            defer { value = nil }
            return value
        }
        await close?()
    }
}

public final class AccountRendezvous<Transport: AccountRouteTransport>: Sendable {
    public let signaling: any SignalingClient
    public let transport: Transport
    public let options: AccountRendezvousOptions

    public init(signaling: any SignalingClient, transport: Transport, options: AccountRendezvousOptions = .init()) {
        self.signaling = signaling
        self.transport = transport
        self.options = options
    }

    /// Pairs with the console: the seed arrives over the cloud and drives a `/sess/rgst`.
    ///
    /// `push` is a push channel that is not running yet, over a socket the caller owns; it is run here and
    /// its loop stopped before returning, and the socket is left for the caller to close.
    public func pair(_ request: AccountRendezvousRequest, push: PushChannel, pushServer: PushServerInfo,
                     accessToken: String) async throws -> Transport.Registration {
        try await run(request, push: push, pushServer: pushServer, accessToken: accessToken,
                      requireSeed: true, keepSessionOpen: false) { outcome in
            guard outcome.associationOpened else {
                throw CloudError.rendezvous(
                    "The control association was never opened, so there is nothing to register over.")
            }
            do {
                let record = try await self.transport.register(context: outcome.context, seed: outcome.seed)
                self.log("registered")
                return record
            } catch {
                self.log("registration failed: \(error)")
                throw error
            }
        }
    }

    /// Reaches an already-paired console over the account route and hands back the live session.
    ///
    /// - Parameter registerFirst: register on the same association before handing it over. The console
    ///   publishes a customData1 on every account-route session, and both captures show rgst, init and ctrl
    ///   on one association, which is what suggests registration is part of each session; the seed is
    ///   waited for only when this is set.
    public func connect(_ request: AccountRendezvousRequest, push: PushChannel, pushServer: PushServerInfo,
                        accessToken: String, registerFirst: Bool) async throws -> AccountConnection<Transport.Registration> {
        try await run(request, push: push, pushServer: pushServer, accessToken: accessToken,
                      requireSeed: registerFirst, keepSessionOpen: true) { outcome in
            guard outcome.associationOpened else {
                self.log("no control association to connect over")
                throw CloudError.rendezvous(
                    "The control association was never opened, so there is nothing to connect over.")
            }

            var registration: Transport.Registration?
            if registerFirst {
                do {
                    registration = try await self.transport.register(context: outcome.context, seed: outcome.seed)
                    self.log("registered on the session's association")
                } catch {
                    self.log("registration on the session's association failed: \(error)")
                    throw error
                }
            }

            let media: @Sendable (Int, UDPEndpoint?) async throws -> MediaEndpoint? = { localPort, reflexive in
                try await self.negotiateMedia(request, outcome: outcome, localPort: localPort, reflexive: reflexive)
            }
            self.log("control association ready for the session")
            return AccountConnection(context: outcome.context, sessionID: outcome.sessionID,
                                     registration: registration, lifetime: outcome.lifetime, media: media)
        }
    }

    // MARK: - The shared sequence

    struct Outcome: Sendable {
        var context: AccountTransportContext
        var seed: [UInt8]
        var sessionID: String
        var lifetime: AccountSessionLifetime
        var nextOffer: @Sendable () async throws -> SignalingMessage?
        var associationOpened: Bool
    }

    /// Everything the push handler shares with the flow. The handler runs on the push loop, so this is the
    /// one place a lock is needed.
    private final class Shared: Sendable {
        struct Counts { var seen = 0, unreadable = 0 }
        let liveSessionID = Mutex<String?>(nil)
        let acked = Mutex<Set<String>>([])
        let counts = Mutex(Counts())
        let seed = OneShot<[UInt8]>()
        let offer = OneShot<SignalingMessage>()
        let joined = OneShot<Void>()
        let mediaOffers = Mailbox<SignalingMessage>()
    }

    private func run<T: Sendable>(_ request: AccountRendezvousRequest, push: PushChannel, pushServer: PushServerInfo,
                                  accessToken: String, requireSeed: Bool, keepSessionOpen: Bool,
                                  finish: @escaping @Sendable (Outcome) async throws -> T) async throws -> T {
        let keyMaterial = try transport.makeSeedKeyMaterial()
        let shared = Shared()
        let transport = self.transport
        let signaling = self.signaling
        let log = self.options.log

        let subscription = push.subscribe { event in
            switch event {
            case .customData1(let value):
                // The first one that opens under our key material wins; a foreign or malformed one is counted
                // and ignored, so a stray frame cannot resolve the wait with garbage.
                if let seed = transport.recoverSeed(customData1: value, keyMaterial: keyMaterial, family: request.family) {
                    shared.counts.withLock { $0.seen += 1 }
                    shared.seed.succeed(seed)
                } else {
                    shared.counts.withLock { $0.seen += 1; $0.unreadable += 1 }
                }

            case .signaling(let message):
                if message.expectsResult {
                    Self.acknowledge(message, shared: shared, signaling: signaling, request: request, log: log)
                }
                // The console's own OFFER (the exchange is symmetric: both sides offer). A repeat of the first
                // is the push channel delivering it twice, which it does routinely; a new sid is the media
                // connection being offered.
                if message.action == "OFFER", let hashed = message.localHashedID, !hashed.isEmpty {
                    if shared.offer.succeed(message) { return }
                    if let first = shared.offer.current, message.sid != first.sid {
                        shared.mediaOffers.post(message)
                    }
                }

            case .consoleJoined:
                shared.joined.succeed(())

            case .frame:
                break
            }
        }

        // Started before the console is triggered, so nothing it publishes the instant it joins is missed.
        let pushLoop = Task { try await push.run(server: pushServer, accessToken: accessToken) }

        let teardown: @Sendable () async -> Void = { [self] in
            push.unsubscribe(subscription)
            // Before the push loop goes down, so the leave is announced on a live connection. Always, success
            // or failure: a session left behind is one stale membership per attempt, and pairing is a thing
            // users retry.
            if let sessionID = shared.liveSessionID.withLock({ $0 }) { await self.leaveQuietly(sessionID) }
            pushLoop.cancel()
            _ = await pushLoop.result
        }

        do {
            let result = try await withTaskCancellationHandler {
                try await sequence(request, shared: shared, keyMaterial: keyMaterial, requireSeed: requireSeed,
                                   push: push, teardown: teardown, finish: finish)
            } onCancel: {
                pushLoop.cancel()
            }
            // Only from here is the caller holding the session; a failure before this still tears down.
            if !keepSessionOpen { await teardown() }
            return result
        } catch {
            await teardown()
            throw error
        }
    }

    private func sequence<T: Sendable>(_ request: AccountRendezvousRequest, shared: Shared, keyMaterial: SeedKeyMaterial,
                                       requireSeed: Bool, push: PushChannel,
                                       teardown: @escaping @Sendable () async -> Void,
                                       finish: @Sendable (Outcome) async throws -> T) async throws -> T {
        // A failed upgrade surfaces here.
        try await push.connected()
        log("push channel connected")

        let sessionID = try await signaling.createSessionID(pushContextID: newPushContextID())
        // Set as soon as the session exists: the acks need it, and the teardown leaves it on every path.
        shared.liveSessionID.withLock { $0 = sessionID }
        log("session created: \(sessionID)")

        try await signaling.sendConnectCommand(consoleDUID: request.consoleDUID, accountID: request.accountID,
                                               sessionID: sessionID, clientType: request.clientType,
                                               seeds: keyMaterial.seeds)
        log("connect command sent (data1/data2)")

        // Joined, waited-and-it-never-came, or never waited: three states, so a failure below cannot claim
        // the console never turned up when nothing looked. Joining is the first moment it can be messaged;
        // it is NOT the moment to offer (see step 7 at the top).
        var consoleJoined: Bool?
        if !request.localHashedID.isEmpty {
            do {
                try await withTimeout(options.offerTimeout) { try await shared.joined.value() }
                consoleJoined = true
                log("console joined the session")
            } catch is TimeoutError {
                consoleJoined = false
                log("the console never joined the session")
                await logMembership(request, sessionID: sessionID, when: "after the join wait")
            }
        }

        // Only registration needs the seed. A connect still sends data1/data2 (the console publishes a
        // customData1 either way), but must not stall on a value it will not use.
        var seed: [UInt8] = []
        if requireSeed {
            do {
                seed = try await withTimeout(options.seedTimeout) { try await shared.seed.value() }
            } catch is TimeoutError {
                let counts = shared.counts.withLock { $0 }
                log("seed wait timed out after \(options.seedTimeout) "
                    + "(customData1 frames seen: \(counts.seen), unreadable: \(counts.unreadable))")
                await logMembership(request, sessionID: sessionID, when: "after the seed wait")
                throw CloudError.rendezvous(Self.seedFailure(consoleJoined: consoleJoined, seen: counts.seen,
                                                            unreadable: counts.unreadable))
            }
            log("registration seed recovered from customData1")
        }

        let consoleOffer: SignalingMessage
        do {
            consoleOffer = try await withTimeout(options.offerTimeout) { try await shared.offer.value() }
        } catch is TimeoutError {
            throw CloudError.rendezvous("The console never offered its candidates, so there is no address to reach it at.")
        }
        log("console OFFER received (\(consoleOffer.candidates.count) candidates)")

        let path = preferredCandidate(consoleOffer, consoleHost: request.consoleHost)
        let context = AccountTransportContext(consoleOffer: consoleOffer, consoleHost: request.consoleHost,
                                              localHashedID: request.localHashedID,
                                              consoleHashedID: consoleOffer.localHashedID ?? [],
                                              selectedCandidate: path)

        var associationOpened = false
        if !request.localHashedID.isEmpty {
            // Now, and not a moment earlier: a sessionMessage reaches only a member, and the console's OFFER
            // is what tells us it is one. Still before the prelude: the console must have seen the id and
            // port we are about to speak from. All three candidates, since off the console's network a lone
            // LOCAL one is a private address on somebody else's.
            try await signaling.sendOffer(sessionID: sessionID, accountID: request.accountID,
                                          consoleDUID: request.consoleDUID,
                                          candidates: Self.ourCandidates(local: request.localEndpoint,
                                                                         reflexive: request.reflexiveEndpoint),
                                          localHashedID: request.localHashedID,
                                          reqID: Self.ourOfferReqID, sid: Self.ourStreamID)

            // Our Init goes out HERE: after our OFFER, before our ACCEPT. Before the OFFER, the console has not
            // seen our id, discards the Init and opens an association of its own; after the whole exchange,
            // our POSTs have taken about two seconds and the console, which initiates ~200 ms after our OFFER,
            // is already retrying. This is where the captured vendor client's Init sits (one POST at t+18.85,
            // its Init at t+20.33, two more POSTs at t+20.34). [X] why the console tolerates the wait there.
            do {
                try await transport.openAssociation(context)
                associationOpened = true
                log("control association opened (after our OFFER, before our ACCEPT)")
            } catch {
                log("could not open the control association: \(error)")
            }

            if let path, let local = request.localEndpoint {
                try await signaling.sendAccept(sessionID: sessionID, accountID: request.accountID,
                                               consoleDUID: request.consoleDUID, reqID: Self.ourAcceptReqID,
                                               sid: Self.ourStreamID, peerSid: consoleOffer.sid,
                                               consoleCandidate: path, localAddress: local.address,
                                               localPort: local.port)
            }
            log("negotiation answered (OFFER, ACCEPT peerSid=\(consoleOffer.sid)); every console message is acked as it arrives")
        }

        let offerTimeout = options.offerTimeout
        let outcome = Outcome(
            context: context, seed: seed, sessionID: sessionID, lifetime: AccountSessionLifetime(close: teardown),
            nextOffer: {
                do { return try await withTimeout(offerTimeout) { try await shared.mediaOffers.next() } } catch is TimeoutError { return nil }
            },
            associationOpened: associationOpened)
        return try await finish(outcome)
    }

    private func negotiateMedia(_ request: AccountRendezvousRequest, outcome: Outcome, localPort: Int,
                                reflexive: UDPEndpoint?) async throws -> MediaEndpoint? {
        guard let mediaOffer = try await outcome.nextOffer() else {
            log("the console never offered a media connection")
            return nil
        }
        guard let path = preferredCandidate(mediaOffer, consoleHost: request.consoleHost),
              let local = request.localEndpoint else { return nil }

        // Our sid and reqIds count on from the control connection's, as the captured client's do for its
        // second stream. The sid is the half that matters: the console takes our id for this leg from the
        // OFFER, and offering stream 1 again made its second connection collide with its first.
        try await signaling.sendOffer(sessionID: outcome.sessionID, accountID: request.accountID,
                                      consoleDUID: request.consoleDUID,
                                      candidates: Self.ourCandidates(local: UDPEndpoint(address: local.address, port: localPort),
                                                                     reflexive: reflexive),
                                      localHashedID: request.localHashedID,
                                      reqID: Self.ourOfferReqID + 2, sid: Self.ourStreamID + 1)
        try await signaling.sendAccept(sessionID: outcome.sessionID, accountID: request.accountID,
                                       consoleDUID: request.consoleDUID, reqID: Self.ourAcceptReqID + 2,
                                       sid: Self.ourStreamID + 1, peerSid: mediaOffer.sid, consoleCandidate: path,
                                       localAddress: local.address, localPort: localPort)
        log("media connection negotiated (peerSid=\(mediaOffer.sid), console \(path.address):\(path.port))")
        return MediaEndpoint(address: path.address, port: path.port, consoleHashedID: mediaOffer.localHashedID ?? [])
    }

    // MARK: - Pieces

    /// Our stream id and the reqIds of our OFFER and ACCEPT for the first connection: the captured client's
    /// 1, 1 and 2. It counts on from these for the media connection.
    static var ourStreamID: Int { 1 }
    static var ourOfferReqID: Int { 1 }
    static var ourAcceptReqID: Int { 2 }

    /// The candidates we offer for one connection, in the captured client's order and labels:
    ///
    /// - `STUN`: the public address and the port the NAT actually assigned, as a STUN server saw it.
    /// - `STATIC`: that public address with our own local port. A guess, good wherever the NAT preserves the
    ///   port; sent only when it differs from the STUN one, since two identical candidates say nothing more.
    /// - `LOCAL`: our address on this network, the only one that works when the peer turns out to be on it.
    public static func ourCandidates(local: UDPEndpoint?, reflexive: UDPEndpoint?) -> [SignalingCandidate] {
        var candidates: [SignalingCandidate] = []
        if let reflexive {
            candidates.append(SignalingCandidate(type: "STUN", address: reflexive.address, port: reflexive.port))
            if let local, reflexive.port != local.port {
                candidates.append(SignalingCandidate(type: "STATIC", address: reflexive.address, port: local.port))
            }
        }
        if let local {
            candidates.append(SignalingCandidate(type: "LOCAL", address: local.address, port: local.port))
        }
        return candidates
    }

    /// Which of the console's candidates to name back: one on our own subnet if there is one (a same-network
    /// session stays on the LAN), else the first parseable one, else the one matching the host we already
    /// had, else the first at all. An offer we cannot parse is still better answered than ignored.
    func preferredCandidate(_ offer: SignalingMessage, consoleHost: String) -> SignalingCandidate? {
        var reflexive: SignalingCandidate?
        for candidate in offer.candidates {
            var parsed = in_addr()
            guard inet_pton(AF_INET, candidate.address, &parsed) == 1 else { continue }
            if transport.sharesSubnetWithLocalInterface(candidate.address) { return candidate }
            if reflexive == nil { reflexive = candidate }
        }
        return reflexive ?? offer.candidates.first { $0.address == consoleHost } ?? offer.candidates.first
    }

    /// The seed-timeout message, ordered by how far upstream the fault is, because the first true statement
    /// is the useful one. A console that never joined cannot have published anything, so blaming the seed
    /// there names a consequence and hides the cause. Signed out of PSN is one such cause and the invisible
    /// one: such a console still answers on the LAN and still looks available in the console list, which
    /// carries no presence field (five runs, 2026-09-24, in the .NET record).
    static func seedFailure(consoleJoined: Bool?, seen: Int, unreadable: Int) -> String {
        if consoleJoined == false && seen == 0 {
            return "The console never joined the session, so it never got as far as publishing a registration "
                + "seed. It has to be awake or able to be woken over the internet from rest mode, reachable by "
                + "the account service, and signed in to PlayStation Network — a console that is signed out "
                + "still answers on your network and still looks available here."
        }
        if unreadable > 0 {
            return "The console published a registration seed (\(unreadable) of \(seen) customData1 frames) but "
                + "none of them could be decrypted with this session's key material."
        }
        if consoleJoined == true {
            return "The console joined the session but did not publish the registration seed (customData1) in time."
        }
        return "The console did not publish the registration seed (customData1) in time."
    }

    /// Answers a console message with a RESULT carrying its reqId, the moment it arrives.
    ///
    /// **The thing that was missing** on the .NET side: Ripcord acked the OFFER and nothing else, and five
    /// captures show the vendor client acking the console's ACCEPT too, within about 1.5 s. Fired from its
    /// own task rather than awaited in the flow, because by the time the ACCEPT comes the flow has moved on
    /// to the transport, and the console gives up about a second later. Deduplicated because the push channel
    /// delivers duplicates routinely (each OFFER arrives twice).
    private static func acknowledge(_ message: SignalingMessage, shared: Shared, signaling: any SignalingClient,
                                    request: AccountRendezvousRequest, log: (@Sendable (String) -> Void)?) {
        guard let sessionID = shared.liveSessionID.withLock({ $0 }) else { return }
        let fresh = shared.acked.withLock { $0.insert("\(message.action)#\(message.reqID)").inserted }
        guard fresh else { return }
        Task {
            do {
                try await signaling.sendResult(sessionID: sessionID, accountID: request.accountID,
                                               consoleDUID: request.consoleDUID, reqID: message.reqID)
                log?("acked the console's \(message.action) (reqId \(message.reqID))")
            } catch {
                log?("could not ack the console's \(message.action) (reqId \(message.reqID)): \(error)")
            }
        }
    }

    /// Asks the session service who is in the session, independently of the push channel: the question a
    /// join timeout cannot answer on its own. If the service lists the console, our subscription missed the
    /// announcement; if only us, the console never acted on the command. Diagnostic only, and never fails.
    private func logMembership(_ request: AccountRendezvousRequest, sessionID: String, when: String) async {
        guard options.log != nil else { return }
        do {
            let sessions = try await signaling.session(id: sessionID)
            guard let mine = sessions.first(where: { $0.sessionID == sessionID }) else {
                log("session readback \(when): the service does not have our session at all")
                return
            }
            let members = mine.members ?? []
            // By the duid we commanded, the only unambiguous test: this client has a deviceUniqueId too.
            let consoleIsMember = members.contains { $0.deviceUniqueID == request.consoleDUID }
            log("session readback \(when): \(members.count) member(s); the console \(consoleIsMember ? "IS" : "is NOT") among them")
            for member in members {
                let isConsole = member.deviceUniqueID == request.consoleDUID
                log("  member platform=\(member.platform) account=\(member.accountID) "
                    + (isConsole ? "<- the console we commanded" : "(not the commanded console)"))
            }
        } catch {
            log("session readback \(when) failed: \(type(of: error))")
        }
    }

    /// Leaves without letting the outcome depend on it, on a task of its own: teardown runs on the
    /// cancellation path too, and a leave skipped because the attempt was cancelled is the leak this prevents.
    private func leaveQuietly(_ sessionID: String) async {
        let signaling = self.signaling
        let outcome = await Task {
            try await withTimeout(CloudTiming.accountLeaveTimeout) { try await signaling.leaveSession(id: sessionID) }
        }.result
        switch outcome {
        case .success: log("left session \(sessionID)")
        case .failure(let error): log("could not leave session \(sessionID): \(error)")
        }
    }

    private func log(_ message: String) { options.log?(message) }
}
