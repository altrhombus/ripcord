// The WAN rendezvous: create the session, wake the console, gather our reflexive candidate on the media
// socket, and re-offer every second until the console's OFFER (its candidates) comes back over the push
// channel. Ported from HalyardWanRendezvous.cs and HalyardWanConnection.cs.
//
// This is the older, simpler route the .NET tree keeps beside the account route (AccountRendezvous), which
// is the one the app drives today. Ported because it is small and its timeline is tested; the UDP steps
// (the STUN gather, the local port) are the transport's.

import Foundation
import Synchronization

public struct WanRequest: Sendable, Equatable {
    public var consoleDUID: String
    public var accountID: String
    /// `Windows`, as .NET. [X] whether another value is accepted.
    public var clientType: String

    public init(consoleDUID: String, accountID: String, clientType: String = "Windows") {
        self.consoleDUID = consoleDUID
        self.accountID = accountID
        self.clientType = clientType
    }
}

public struct WanRendezvousOptions: Sendable {
    /// How long to keep offering before giving up.
    public var rendezvousTimeout: Duration = .seconds(30)
    /// How often to re-send our OFFER. The console can take seconds to wake and join, and the observed client
    /// re-sends its OFFER throughout that window rather than once.
    public var offerInterval: Duration = .seconds(1)
    /// Progress lines for a harness; also switches on the session readbacks.
    public var log: (@Sendable (String) -> Void)?

    public init(rendezvousTimeout: Duration = .seconds(30), offerInterval: Duration = .seconds(1),
                log: (@Sendable (String) -> Void)? = nil) {
        self.rendezvousTimeout = rendezvousTimeout
        self.offerInterval = offerInterval
        self.log = log
    }
}

/// A completed WAN rendezvous: the console's candidates, and the session and push channel, which stay live
/// for the stream (the push channel carries later signaling, and the session must be left on disconnect).
/// `close()` is the disconnect.
public final class WanConnection: Sendable {
    public let sessionID: String
    /// In the order the console advertised them.
    public let consoleCandidates: [SignalingCandidate]
    /// Still running, for a session that wants to watch later signaling.
    public let push: PushChannel

    private let signaling: any SignalingClient
    private let pushLoop: Task<Void, any Error>
    private let closed = Mutex(false)

    init(sessionID: String, consoleCandidates: [SignalingCandidate], signaling: any SignalingClient,
         push: PushChannel, pushLoop: Task<Void, any Error>) {
        self.sessionID = sessionID
        self.consoleCandidates = consoleCandidates
        self.signaling = signaling
        self.push = push
        self.pushLoop = pushLoop
    }

    /// Leaves the session (best effort, 3 s, never throws), then stops the push loop and closes its socket.
    public func close() async {
        let first = closed.withLock { done -> Bool in
            defer { done = true }
            return !done
        }
        guard first else { return }
        let signaling = self.signaling, sessionID = self.sessionID
        _ = await Task {
            try await withTimeout(CloudTiming.wanLeaveTimeout) { try await signaling.leaveSession(id: sessionID) }
        }.result
        pushLoop.cancel()
        _ = await pushLoop.result
        await push.close()
    }
}

public final class WanRendezvous: Sendable {
    public let signaling: any SignalingClient
    public let transport: any WanRendezvousTransport
    public let options: WanRendezvousOptions

    public init(signaling: any SignalingClient, transport: any WanRendezvousTransport,
                options: WanRendezvousOptions = .init()) {
        self.signaling = signaling
        self.transport = transport
        self.options = options
    }

    /// Runs the rendezvous. `push` is a push channel that is not running yet; on success the returned
    /// connection owns it, and on failure it is closed here.
    public func connect(_ request: WanRequest, push: PushChannel, pushServer: PushServerInfo,
                        accessToken: String) async throws -> WanConnection {
        let consoleOffer = OneShot<[SignalingCandidate]>()
        let subscription = push.subscribe { event in
            if case .signaling(let message) = event, message.isConsoleOffer { consoleOffer.succeed(message.candidates) }
        }

        // Receiving before the console is triggered, so an OFFER the instant it joins is not missed.
        let pushLoop = Task { try await push.run(server: pushServer, accessToken: accessToken) }

        do {
            return try await withTaskCancellationHandler {
                // The session's message channel binds to the account's live push connection at create time,
                // so the upgrade must have succeeded first, or the OFFER POST 404s. The vendor's order too.
                try await push.connected()

                let sessionID = try await signaling.createSessionID(pushContextID: newPushContextID())
                log("session created: \(sessionID)")

                // A create that returns an id whose session is then unreadable is the difference between "the
                // console won't answer" and "there is nothing to answer to"; only a readback tells them apart.
                await readback(sessionID, when: "after create")

                // Random seeds of the right shape: their role on this route is [X] and nothing here decrypts.
                try await signaling.sendConnectCommand(consoleDUID: request.consoleDUID, accountID: request.accountID,
                                                       sessionID: sessionID, clientType: request.clientType,
                                                       seeds: .random())
                log("wake command sent")

                let ours = await ourCandidates()
                log("gathered \(ours.count) local candidate(s): "
                    + ours.map { "\($0.type) \($0.address):\($0.port)" }.joined(separator: ", "))

                let candidates = try await offerUntilAnswered(request, sessionID: sessionID, ours: ours,
                                                              consoleOffer: consoleOffer)
                return WanConnection(sessionID: sessionID, consoleCandidates: candidates, signaling: signaling,
                                     push: push, pushLoop: pushLoop)
            } onCancel: {
                pushLoop.cancel()
            }
        } catch {
            push.unsubscribe(subscription)
            pushLoop.cancel()
            _ = await pushLoop.result
            await push.close()
            throw error
        }
    }

    /// The reflexive one first (as the console orders its own), then the LAN one, which is always offered:
    /// on the same network it is the fast path, and off it it costs nothing.
    func ourCandidates() async -> [SignalingCandidate] {
        var candidates: [SignalingCandidate] = []
        if let reflexive = await transport.gatherReflexive() {
            candidates.append(SignalingCandidate(type: "STATIC", address: reflexive.address, port: reflexive.port))
        }
        if let lan = transport.lanAddress() {
            candidates.append(SignalingCandidate(type: "LOCAL", address: lan, port: transport.localPort))
        }
        return candidates
    }

    private func offerUntilAnswered(_ request: WanRequest, sessionID: String, ours: [SignalingCandidate],
                                    consoleOffer: OneShot<[SignalingCandidate]>) async throws -> [SignalingCandidate] {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: options.rendezvousTimeout)
        var lastOfferError: CloudError?
        var round = 0

        while true {
            try Task.checkCancellation()
            do {
                try await signaling.sendOffer(sessionID: sessionID, accountID: request.accountID,
                                              consoleDUID: request.consoleDUID, candidates: ours, localHashedID: [],
                                              reqID: 1, sid: 1)
                log("offer #\(round + 1) sent")
            } catch let error as CloudError where error.isServiceError {
                // A just-created session can take a moment to be addressable across PSN's backend, so an early
                // OFFER can 404. Keep offering; the timeout still bounds it, and the last error is reported.
                lastOfferError = error
                log("offer #\(round + 1) failed: \(error)")
            }

            // Every few rounds, read the session back, so a live run can tell "not joined yet" from a dead end.
            round += 1
            if round % 3 == 0 { await readback(sessionID, when: "round \(round)") }

            do {
                return try await withTimeout(options.offerInterval) { try await consoleOffer.value() }
            } catch is TimeoutError {}

            if clock.now >= deadline {
                throw CloudError.rendezvous(lastOfferError.map {
                    "Never completed the OFFER exchange within the timeout; the last OFFER POST failed: \($0)"
                } ?? "The console did not answer over the push channel within the rendezvous timeout. It may be "
                    + "offline, remote-wake may be disabled, or it rejected our OFFER.")
            }
        }
    }

    /// What the account service sees: whether our session is there, how many members, whether the console
    /// (a non-REMOTE_PLAY member) has joined. Only when logging, and never throws.
    private func readback(_ sessionID: String, when: String) async {
        guard options.log != nil else { return }
        do {
            let sessions = try await signaling.session(id: sessionID)
            guard let ours = sessions.first(where: { $0.sessionID.caseInsensitiveCompare(sessionID) == .orderedSame }) else {
                log("readback \(when): OUR SESSION IS NOT PRESENT among \(sessions.count) account session(s) — create did not stick")
                return
            }
            let members = ours.members ?? []
            let joined = members.contains { $0.platform.caseInsensitiveCompare("REMOTE_PLAY") != .orderedSame }
            log("readback \(when): session present, \(members.count) member(s), console joined: \(joined)")
        } catch {
            log("readback \(when): failed (\(error))")
        }
    }

    private func log(_ message: String) { options.log?(message) }
}
