// The rendezvous route's meeting point between the session thread and Swift's cloud work.
//
// halyard_client.h's "THE RENDEZVOUS ROUTE" interleaves two timelines at exact points: the UDP one, which
// libripcord runs on the session's one host-owned thread, and the cloud one (sign-in, the session, the push
// channel, OFFER/ACCEPT/RESULT), which is async Swift. Neither may drive the other's calls: the core is not
// thread-safe and the cloud work must not block a thread. So the session thread prepares the control leg,
// hands this link to the route's `drive` closure on a task of its own, and then waits on the link, running
// what the cloud work asks for as it asks for it:
//
//   drive (Swift, async)                       the session thread (C)
//   ------------------------------------       ---------------------------------------------------------
//                                              halyard_client_rendezvous_prepare  -> link.controlLeg
//   cloud session, command, console's OFFER,
//   OUR OFFER (link.controlLeg's port/mapping)
//   transport.openAssociation  --- begin -->   halyard_client_rendezvous_begin
//   OUR ACCEPT
//   transport.register (optional) - rgst -->   halyard_account_regist_run over halyard_client_rendezvous_exchange
//   link.proceed(media:onEnd:)  -- proceed ->  halyard_client_connect ... poll_media, repeatedly:
//     media(leg) on a task of its own  <----     the A/V leg, bound and STUN-asked
//     (next OFFER, our OFFER, our ACCEPT)
//     ... answer ------------------------->      1 with the console's A/V endpoint, and on to the stream
//                                              halyard_client_pump until the end, halyard_client_destroy
//   onEnd (leave the cloud session)  <------   after destroy, as step 8 orders
//
// Every hand-over is a Mutex-protected slot, as ConsoleSession's pad and passcode are: Swift posts, the
// session thread polls, and the answer goes back through a continuation. The session thread takes no
// Swift lock while in C.

internal import CLibripcord
import Darwin
import Foundation
import Synchronization

/// A STUN server by name. Resolved to IPv4 on the session thread when a rendezvous session starts, since the
/// core resolves no names (rc_stun.h) - and only then, so building a configuration touches no network.
public struct StunServer: Sendable, Hashable, CustomStringConvertible {
    public var host: String
    public var port: UInt16

    public init(host: String, port: UInt16) {
        self.host = host
        self.port = port
    }

    public var description: String { "\(host):\(port)" }

    /// StunClient.DefaultServers' three, reordered so the first two are different operators: the core asks
    /// in order until two answer and classifies the NAT from the pair, and two names of one provider can be
    /// one host, which would make a symmetric NAT look consistent (rc_stun.h, rc_stun_discover_mapping).
    public static let defaults = [
        StunServer(host: "stun.l.google.com", port: 19302),
        StunServer(host: "stun.cloudflare.com", port: 3478),
        StunServer(host: "stun1.l.google.com", port: 19302),
    ]

    /// The first IPv4 address of each server that resolves, at most `limit` of them. A name that does not
    /// resolve is skipped, as .NET's ResolveAsync skips it.
    static func resolve(_ servers: [StunServer], limit: Int = Int(HALYARD_CLIENT_STUN_MAX)) -> [sockaddr_in] {
        var out: [sockaddr_in] = []
        for server in servers where out.count < limit {
            var hints = addrinfo()
            hints.ai_family = AF_INET
            hints.ai_socktype = SOCK_DGRAM
            var list: UnsafeMutablePointer<addrinfo>?
            guard getaddrinfo(server.host, String(server.port), &hints, &list) == 0, let first = list else { continue }
            defer { freeaddrinfo(list) }
            if let addr = first.pointee.ai_addr, addr.pointee.sa_family == sa_family_t(AF_INET) {
                out.append(addr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee })
            }
        }
        return out
    }
}

/// One of our rendezvous legs, as an OFFER needs it (halyard_client_leg).
public struct RendezvousLeg: Sendable, Equatable {
    /// The bound socket's port.
    public var localPort: Int
    /// What STUN said the NAT maps it to, when a server answered.
    public var reflexive: UDPEndpoint?
    /// True when two servers agreed, false for a per-destination (symmetric) NAT that a distant console will
    /// not get through, nil when fewer than two answered.
    public var endpointIndependent: Bool?

    public init(localPort: Int, reflexive: UDPEndpoint? = nil, endpointIndependent: Bool? = nil) {
        self.localPort = localPort
        self.reflexive = reflexive
        self.endpointIndependent = endpointIndependent
    }

    init(_ c: halyard_client_leg) {
        localPort = Int(c.local_port)
        if c.has_reflexive != 0 {
            let a = c.reflexive_address
            reflexive = UDPEndpoint(address: "\(a.0).\(a.1).\(a.2).\(a.3)", port: Int(c.reflexive_port))
        }
        endpointIndependent = c.endpoint_independent < 0 ? nil : c.endpoint_independent == 1
    }
}

/// How a session reaches the console over the internet (halyard_client.h, RENDEZVOUS). Everything the
/// cloud tier knows before a connect starts; what it learns during one comes back through the link.
public struct RendezvousRoute: Sendable {
    /// Asked in order, until two answer, per leg. Empty: no STUN, and each leg offers only its LOCAL candidate.
    public var stunServers: [StunServer]
    /// The address both legs bind (dotted quad). Nil: INADDR_ANY. A test binds 127.0.0.1.
    public var bindAddress: String?
    /// 0 lets the system choose.
    public var controlLocalPort: UInt16 = 0
    public var mediaLocalPort: UInt16 = 0
    /// How long poll_media may say "not yet"; each 9303 stage; the quiet window before a re-send. Zero takes
    /// the core's defaults (30 s, 30 s, 5 s).
    public var mediaOfferTimeout: Duration = .zero
    public var dgramStageTimeout: Duration = .zero
    public var dgramReceiveTimeout: Duration = .zero
    /// How long the session thread waits for `drive` to reach `proceed` before giving up. The rendezvous has
    /// its own timeouts (seed, OFFER); this is the outer bound on a cloud tier that never answers at all.
    public var driveTimeout: Duration = .seconds(180)
    /// The cloud half. Runs on a task of its own once the control leg is prepared, and must end by calling
    /// `link.proceed` (the stream goes ahead) or throwing (it does not). Returning without proceeding ends
    /// the session as a rendezvous failure.
    public var drive: @Sendable (RendezvousLink) async throws -> Void

    public init(stunServers: [StunServer] = StunServer.defaults, bindAddress: String? = nil,
                drive: @escaping @Sendable (RendezvousLink) async throws -> Void) {
        self.stunServers = stunServers
        self.bindAddress = bindAddress
        self.drive = drive
    }
}

public final class RendezvousLink: Sendable {
    /// Our control leg, bound and STUN-asked: what our first OFFER advertises.
    public let controlLeg: RendezvousLeg

    /// The WAN rendezvous's transport over that same leg.
    public var wanTransport: any WanRendezvousTransport { LegWanTransport(leg: controlLeg) }

    /// The account route's transport over this session's association.
    public func accountTransport(family: ConsoleFamily, accountID: String) -> SessionAccountTransport {
        SessionAccountTransport(link: self, family: family, accountID: accountID)
    }

    /// Lets the stream go ahead: connect, then pump. `media` is the A/V leg's negotiation (for the account
    /// route, `AccountConnection.negotiateMedia`), called once, off the session thread, with our A/V leg; nil
    /// or a throw means there is no A/V path. `onEnd` runs after the session's sockets are closed: leave the
    /// cloud session there, never before (halyard_client.h, step 8).
    public func proceed(media: @escaping @Sendable (RendezvousLeg) async throws -> MediaEndpoint?,
                        onEnd: @escaping @Sendable () async -> Void) {
        post(.proceed(Proceed(media: media, onEnd: onEnd)))
    }

    /// Ends the session before it connects, for `reason` (reported as the outcome's failure).
    public func abandon(_ reason: String) { post(.abandon(reason)) }

    // MARK: - Internals

    struct Proceed: Sendable {
        let media: @Sendable (RendezvousLeg) async throws -> MediaEndpoint?
        let onEnd: @Sendable () async -> Void
    }

    struct BeginRequest: Sendable {
        let localHashedID: [UInt8]
        let path: ConsolePath
        let consoleHashedID: [UInt8]
    }

    struct RegisterRequest: Sendable {
        let context: AccountTransportContext
        let seed: [UInt8]
        let family: ConsoleFamily
        let accountID: String
    }

    enum Job: Sendable {
        case begin(BeginRequest, CheckedContinuation<Void, any Error>)
        case register(RegisterRequest, CheckedContinuation<PairedConsole, any Error>)
        case proceed(Proceed)
        case abandon(String)
    }

    enum MediaState: Sendable {
        case notAsked, pending, answered(MediaEndpoint?)
    }

    private struct State {
        var jobs: [Job] = []
        /// Set once the session thread takes no more jobs: later ones fail at once.
        var closed: String?
        var media = MediaState.notAsked
    }

    private let state = Mutex(State())

    init(controlLeg: RendezvousLeg) { self.controlLeg = controlLeg }

    func begin(_ context: AccountTransportContext) async throws {
        try validateHashedIDs(context)
        guard let path = ConsolePath.control(context) else {
            throw PairingError.associationFailed("the console offered no IPv4 candidate, and there is no host to fall back to")
        }
        let request = BeginRequest(localHashedID: context.localHashedID, path: path, consoleHashedID: context.consoleHashedID)
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            post(.begin(request, continuation))
        }
    }

    func register(context: AccountTransportContext, seed: [UInt8], family: ConsoleFamily, accountID: String) async throws -> PairedConsole {
        let request = RegisterRequest(context: context, seed: seed, family: family, accountID: accountID)
        return try await withCheckedThrowingContinuation { continuation in
            post(.register(request, continuation))
        }
    }

    private func post(_ job: Job) {
        let refused = state.withLock { s -> String? in
            if let closed = s.closed { return closed }
            s.jobs.append(job)
            return nil
        }
        if let refused { Self.refuse(job, refused) }
    }

    /// The session thread's side: the next job, if any.
    func takeJob() -> Job? {
        state.withLock { s in s.jobs.isEmpty ? nil : s.jobs.removeFirst() }
    }

    /// No more jobs: anything still queued, and anything posted later, fails with `reason`.
    func close(_ reason: String) {
        let pending = state.withLock { s -> [Job] in
            if s.closed == nil { s.closed = reason }
            defer { s.jobs.removeAll() }
            return s.jobs
        }
        for job in pending { Self.refuse(job, reason) }
    }

    private static func refuse(_ job: Job, _ reason: String) {
        switch job {
        case .begin(_, let c): c.resume(throwing: CloudError.rendezvous(reason))
        case .register(_, let c): c.resume(throwing: CloudError.rendezvous(reason))
        case .proceed, .abandon: break
        }
    }

    /// poll_media's state, read and advanced on the session thread; answered from the negotiation's task.
    func mediaState() -> MediaState { state.withLock { $0.media } }
    func markMediaPending() { state.withLock { $0.media = .pending } }
    func answerMedia(_ endpoint: MediaEndpoint?) { state.withLock { $0.media = .answered(endpoint) } }
}
