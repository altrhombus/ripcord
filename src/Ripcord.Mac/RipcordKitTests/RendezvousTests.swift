// The two rendezvous timelines, driven entirely by fakes: a scripted push socket, a signaling client that
// records every call in order and plays the console's part, and a transport standing in for the C side.
// Ported from HalyardWanRendezvousTests.cs, HalyardAccountPairingTests.cs and AccountCandidateTests.cs,
// plus the ordering the .NET tests leave implicit and the account route depends on: our datagram Init after
// our OFFER and before our ACCEPT.
//
// What is pinned is what is invisible until a live connect fails: listening before triggering the console,
// creating the session only once the push upgrade has succeeded, re-offering while the console joins,
// timing out rather than hanging, acknowledging every console message exactly once, and leaving the session
// on every path that owns it.

import Foundation
@testable import RipcordKit
import Synchronization
import Testing

// MARK: - Fakes

/// Records every signaling call, in order, and lets a test hang the console's behaviour off them.
final class FakeSignaling: SignalingClient {
    struct Offer: Sendable, Equatable {
        var candidates: [SignalingCandidate]
        var localHashedID: [UInt8]
        var reqID: Int
        var sid: Int
    }

    struct Accept: Sendable, Equatable {
        var reqID: Int, sid: Int, peerSid: Int
        var consoleCandidate: SignalingCandidate
        var localAddress: String, localPort: Int
    }

    private struct State {
        var calls: [String] = []
        var offers: [Offer] = []
        var accepts: [Accept] = []
        var results: [Int] = []
        var seeds: ConnectSeeds?
        var offerFailuresLeft = 0
    }

    private let state = Mutex(State())
    let sessionID: String
    /// Runs when the command is sent, with its seeds: the console waking up.
    let onCommand: (@Sendable (ConnectSeeds) -> Void)?
    /// Runs after each OFFER POST that succeeds, with the number sent so far.
    let onOffer: (@Sendable (Int) -> Void)?
    /// Runs as the session is created, so a test can see what was true at that moment.
    let onCreate: (@Sendable () -> Void)?

    init(sessionID: String = "session-1", offerFailures: Int = 0,
         onCreate: (@Sendable () -> Void)? = nil,
         onCommand: (@Sendable (ConnectSeeds) -> Void)? = nil,
         onOffer: (@Sendable (Int) -> Void)? = nil) {
        self.sessionID = sessionID
        self.onCreate = onCreate
        self.onCommand = onCommand
        self.onOffer = onOffer
        state.withLock { $0.offerFailuresLeft = offerFailures }
    }

    var calls: [String] { state.withLock { $0.calls } }
    var offers: [Offer] { state.withLock { $0.offers } }
    var accepts: [Accept] { state.withLock { $0.accepts } }
    var results: [Int] { state.withLock { $0.results } }
    var seeds: ConnectSeeds? { state.withLock { $0.seeds } }
    /// The calls in order with the acks left out, since those are sent from tasks of their own.
    var timeline: [String] { calls.filter { !$0.hasPrefix("result") } }

    private func record(_ call: String) { state.withLock { $0.calls.append(call) } }

    func createSessionID(pushContextID: String) async throws -> String {
        onCreate?()
        record("create")
        return sessionID
    }

    func sendConnectCommand(consoleDUID: String, accountID: String, sessionID: String, clientType: String,
                            seeds: ConnectSeeds) async throws -> String {
        state.withLock { $0.seeds = seeds }
        record("command")
        onCommand?(seeds)
        return "command-1"
    }

    func sendOffer(sessionID: String, accountID: String, consoleDUID: String, candidates: [SignalingCandidate],
                   localHashedID: [UInt8], reqID: Int, sid: Int) async throws {
        let (fail, count) = state.withLock { s -> (Bool, Int) in
            s.calls.append("offer sid=\(sid) reqId=\(reqID)")
            if s.offerFailuresLeft > 0 {
                s.offerFailuresLeft -= 1
                return (true, 0)
            }
            s.offers.append(Offer(candidates: candidates, localHashedID: localHashedID, reqID: reqID, sid: sid))
            return (false, s.offers.count)
        }
        if fail {
            throw CloudError.requestFailed(method: "POST", url: ".../sessionMessage", status: 404, body: nil)
        }
        onOffer?(count)
    }

    func session(id sessionID: String) async throws -> [CloudSession] {
        [CloudSession(sessionID: self.sessionID, members: [SessionMember(accountID: "42", platform: "REMOTE_PLAY",
                                                                         deviceUniqueID: "me")])]
    }

    func sendResult(sessionID: String, accountID: String, consoleDUID: String, reqID: Int) async throws {
        state.withLock {
            $0.calls.append("result \(reqID)")
            $0.results.append(reqID)
        }
    }

    func sendAccept(sessionID: String, accountID: String, consoleDUID: String, reqID: Int, sid: Int, peerSid: Int,
                    consoleCandidate: SignalingCandidate, localAddress: String, localPort: Int) async throws {
        state.withLock {
            $0.calls.append("accept sid=\(sid) reqId=\(reqID) peerSid=\(peerSid)")
            $0.accepts.append(Accept(reqID: reqID, sid: sid, peerSid: peerSid, consoleCandidate: consoleCandidate,
                                     localAddress: localAddress, localPort: localPort))
        }
    }

    func leaveSession(id sessionID: String) async throws { record("leave") }
}

/// The C side's stand-in. Its "seed crypto" is a XOR under data1, which is enough for the timeline: what is
/// tested here is that the value published after the command is the one registration receives, not the
/// cipher (libripcord's tests own that).
final class FakeAccountTransport: AccountRouteTransport {
    typealias Registration = [UInt8]

    let signaling: FakeSignaling?
    let failOpen: Bool
    private let state = Mutex<(opened: [AccountTransportContext], registered: [[UInt8]])>(([], []))

    init(recordingInto signaling: FakeSignaling? = nil, failOpen: Bool = false) {
        self.signaling = signaling
        self.failOpen = failOpen
    }

    var opened: [AccountTransportContext] { state.withLock { $0.opened } }
    var registered: [[UInt8]] { state.withLock { $0.registered } }

    static let keyMaterial = SeedKeyMaterial(data1: Array(repeating: 0x5A, count: 16), data2: Array(repeating: 0xA5, count: 16))

    static func seal(_ seed: [UInt8], data1: [UInt8]) -> String {
        Data(zip(seed, data1).map { $0 ^ $1 }).base64EncodedString()
    }

    func makeSeedKeyMaterial() throws -> SeedKeyMaterial { Self.keyMaterial }

    func recoverSeed(customData1: String, keyMaterial: SeedKeyMaterial, family: ConsoleFamily) -> [UInt8]? {
        guard let data = Data(base64Encoded: customData1), data.count == 16 else { return nil }
        return zip(data, keyMaterial.data1).map { $0 ^ $1 }
    }

    func openAssociation(_ context: AccountTransportContext) async throws {
        signaling?.noteTransport("open")
        state.withLock { $0.opened.append(context) }
        if failOpen { throw CloudError.webSocket("no route") }
    }

    func register(context: AccountTransportContext, seed: [UInt8]) async throws -> [UInt8] {
        signaling?.noteTransport("register")
        state.withLock { $0.registered.append(seed) }
        return [1, 2, 3, 4]
    }

    /// The test's own subnet rule: 192.168.1.x is "here".
    func sharesSubnetWithLocalInterface(_ address: String) -> Bool { address.hasPrefix("192.168.1.") }
}

extension FakeSignaling {
    /// Lets the fake transport write its steps into the same timeline as the signaling calls.
    func noteTransport(_ step: String) { record(step) }
}

final class FakeWanTransport: WanRendezvousTransport {
    let reflexive: UDPEndpoint?
    init(reflexive: UDPEndpoint?) { self.reflexive = reflexive }
    var localPort: Int { 50_000 }
    func gatherReflexive() async -> UDPEndpoint? { reflexive }
    func lanAddress() -> String? { "192.168.1.9" }
}

// MARK: - The WAN rendezvous

@Suite("Cloud: the WAN rendezvous")
struct WanRendezvousTests {
    let server = PushServerInfo(fqdn: "host")
    let request = WanRequest(consoleDUID: "console-duid", accountID: "account-42")

    static func offerFrame(port: Int = 9303) -> String {
        Frames.signaling(from: "PROSPERO",
                         #"{"action":"OFFER","reqId":1,"error":0,"connRequest":{"candidate":[{"type":"STATIC","addr":"203.0.113.7","mappedAddr":"0.0.0.0","port":"#
                         + String(port) + #","mappedPort":0}]}}"#)
    }

    private func connect(_ signaling: FakeSignaling, _ socket: FakeWebSocket,
                         reflexive: UDPEndpoint? = nil, options: WanRendezvousOptions = .init()) async throws -> WanConnection {
        let rendezvous = WanRendezvous(signaling: signaling, transport: FakeWanTransport(reflexive: reflexive), options: options)
        return try await withTimeout(.seconds(10)) { [server, request] in
            try await rendezvous.connect(request, push: PushChannel(socket: socket), pushServer: server, accessToken: "token")
        }
    }

    @Test("returns the console's candidates once it answers")
    func answers() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: { _ in socket.push(Self.offerFrame()) })
        let connection = try await connect(signaling, socket, reflexive: UDPEndpoint(address: "198.51.100.9", port: 40000))
        #expect(connection.sessionID == "session-1")
        #expect(connection.consoleCandidates == [SignalingCandidate(type: "STATIC", address: "203.0.113.7", port: 9303)])
        await connection.close()
    }

    @Test("offers both our reflexive and our LAN candidate, reflexive first")
    func offersBoth() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: { _ in socket.push(Self.offerFrame()) })
        let connection = try await connect(signaling, socket, reflexive: UDPEndpoint(address: "198.51.100.9", port: 40000))
        #expect(signaling.offers.last?.candidates == [SignalingCandidate(type: "STATIC", address: "198.51.100.9", port: 40000),
                                                      SignalingCandidate(type: "LOCAL", address: "192.168.1.9", port: 50_000)])
        await connection.close()
    }

    @Test("offers LAN only when STUN finds nothing")
    func lanOnly() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: { _ in socket.push(Self.offerFrame()) })
        let connection = try await connect(signaling, socket)
        #expect(signaling.offers.last?.candidates.map(\.type) == ["LOCAL"])
        await connection.close()
    }

    @Test("listens before triggering the console, so an early OFFER is not lost")
    func listensFirst() async throws {
        let socket = FakeWebSocket()
        let connectedAtCommand = Mutex(false)
        let signaling = FakeSignaling(onCommand: { _ in
            connectedAtCommand.withLock { $0 = socket.isConnected }
            socket.push(Self.offerFrame())
        })
        let connection = try await connect(signaling, socket)
        #expect(connectedAtCommand.withLock { $0 })
        await connection.close()
    }

    @Test("does not create the session until the push connection is up")
    func waitsForPush() async throws {
        let socket = FakeWebSocket(gated: true)
        let signaling = FakeSignaling(onCommand: { _ in socket.push(Self.offerFrame()) })
        let running = Task { [self] in try await connect(signaling, socket) }
        try await Task.sleep(for: .milliseconds(100))
        #expect(!signaling.calls.contains("create"))
        socket.connectGate?.succeed(())
        let connection = try await running.value
        #expect(signaling.calls.contains("create"))
        await connection.close()
    }

    @Test("a transient 404 on the OFFER does not abort; the loop keeps trying until the console answers")
    func transient404() async throws {
        let socket = FakeWebSocket()
        // The console answers because an OFFER finally reached it, so both failures are behind us by construction.
        let signaling = FakeSignaling(offerFailures: 2, onOffer: { count in if count == 1 { socket.push(Self.offerFrame()) } })
        let connection = try await connect(signaling, socket, options: WanRendezvousOptions(offerInterval: .milliseconds(40)))
        #expect(signaling.calls.filter { $0.hasPrefix("offer") }.count >= 3)
        #expect(connection.consoleCandidates.count == 1)
        await connection.close()
    }

    @Test("keeps re-offering while the console takes time to join")
    func reoffers() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onOffer: { count in if count == 2 { socket.push(Self.offerFrame()) } })
        let connection = try await connect(signaling, socket, options: WanRendezvousOptions(offerInterval: .milliseconds(40)))
        #expect(signaling.offers.count >= 2)
        await connection.close()
    }

    @Test("re-offers every interval, with a readback every third round, until the timeout")
    func timing() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling()
        let lines = Mutex<[String]>([])
        let options = WanRendezvousOptions(rendezvousTimeout: .milliseconds(250), offerInterval: .milliseconds(100),
                                           log: { line in lines.withLock { $0.append(line) } })
        let started = ContinuousClock.now
        await #expect(throws: CloudError.self) { try await connect(signaling, socket, options: options) }
        let elapsed = ContinuousClock.now - started
        // On an idle machine: offers at 0, 100 and 200 ms, and the deadline noticed at the end of the third
        // interval. Each round waits AT LEAST one interval, so three is a ceiling. A loaded CI runner's timers
        // overrun, the deadline arrives after fewer rounds, and an exact count failed there (1.17 s for this
        // test), so what is asserted is what holds under any scheduling.
        let offers = signaling.offers.count
        #expect((1...3).contains(offers))
        #expect(elapsed >= .milliseconds(250) && elapsed < .seconds(5))
        #expect(lines.withLock { $0.filter { $0.hasPrefix("readback round") }.count } == offers / 3)
        #expect(lines.withLock { $0.contains { $0.hasPrefix("readback after create") } })
    }

    @Test("times out when the console never answers, and tears everything down")
    func timesOut() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling()
        await #expect(throws: CloudError.self) {
            try await connect(signaling, socket, options: WanRendezvousOptions(rendezvousTimeout: .milliseconds(150),
                                                                              offerInterval: .milliseconds(40)))
        }
        #expect(signaling.calls.contains("create"))
        #expect(socket.wasClosed)
    }

    @Test("disconnect leaves the session")
    func disconnectLeaves() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: { _ in socket.push(Self.offerFrame()) })
        let connection = try await connect(signaling, socket)
        #expect(!signaling.calls.contains("leave"))
        await connection.close()
        #expect(signaling.calls.contains("leave"))
        #expect(socket.wasClosed)
    }

    @Test("cancellation aborts the rendezvous")
    func cancellation() async throws {
        let socket = FakeWebSocket()
        let rendezvous = WanRendezvous(signaling: FakeSignaling(), transport: FakeWanTransport(reflexive: nil))
        let running = Task { [server, request] in
            try await rendezvous.connect(request, push: PushChannel(socket: socket), pushServer: server, accessToken: "token")
        }
        try await Task.sleep(for: .milliseconds(50))
        running.cancel()
        await #expect(throws: (any Error).self) { try await running.value }
        #expect(socket.wasClosed)
    }
}

// MARK: - The account route

@Suite("Cloud: the account rendezvous")
struct AccountRendezvousTests {
    let server = PushServerInfo(fqdn: "abc-pushcl.np.communication.playstation.net")
    static let consoleSid = 24043
    static let consoleHashedID: [UInt8] = Array(1...20)
    static let ourHashedID: [UInt8] = Array(repeating: 0x33, count: 20)

    var request: AccountRendezvousRequest {
        AccountRendezvousRequest(consoleID: "console-1", consoleHost: "192.168.1.50", consoleDUID: "duid-1",
                                 accountID: "acct-1", clientDeviceID: syntheticDeviceID, localHashedID: Self.ourHashedID,
                                 localEndpoint: UDPEndpoint(address: "192.168.1.9", port: 51000))
    }

    /// The console: joins, publishes the seed sealed under our data1, and offers.
    static func console(_ socket: FakeWebSocket, seed: [UInt8], join: Bool = true, offer: Bool = true,
                        customData1: String? = nil) -> @Sendable (ConnectSeeds) -> Void {
        { seeds in
            if join { socket.push(Frames.joined()) }
            let data1 = Array(Data(base64Encoded: seeds.data1) ?? Data())
            socket.push(Frames.customData1(customData1 ?? FakeAccountTransport.seal(seed, data1: data1)))
            if offer { socket.push(Frames.accountOffer(hashedID: consoleHashedID, sid: consoleSid)) }
        }
    }

    private func pair(_ signaling: FakeSignaling, _ transport: FakeAccountTransport, _ socket: FakeWebSocket,
                      request: AccountRendezvousRequest? = nil,
                      options: AccountRendezvousOptions = .init()) async throws -> [UInt8] {
        let rendezvous = AccountRendezvous(signaling: signaling, transport: transport, options: options)
        let request = request ?? self.request
        return try await withTimeout(.seconds(10)) { [server] in
            try await rendezvous.pair(request, push: PushChannel(socket: socket), pushServer: server, accessToken: "token")
        }
    }

    @Test("pairing recovers the seed from customData1 and registers with it")
    func pairs() async throws {
        let socket = FakeWebSocket()
        let seed: [UInt8] = Array(100..<116)
        let signaling = FakeSignaling(onCommand: Self.console(socket, seed: seed))
        let transport = FakeAccountTransport(recordingInto: signaling)

        let record = try await pair(signaling, transport, socket)

        #expect(record == [1, 2, 3, 4])
        #expect(transport.registered == [seed])                    // the recovered seed drove registration
        #expect(signaling.seeds?.data1 == Data(FakeAccountTransport.keyMaterial.data1).base64EncodedString())
        #expect(signaling.seeds?.data2 == Data(FakeAccountTransport.keyMaterial.data2).base64EncodedString())
        #expect(signaling.seeds?.data3 == "")
        #expect(signaling.offers.first?.localHashedID == Self.ourHashedID)
        let context = try #require(transport.opened.first)
        #expect(context.consoleHashedID == Self.consoleHashedID)
        #expect(context.localHashedID == Self.ourHashedID)
        #expect(context.selectedCandidate == SignalingCandidate(type: "LOCAL", address: "192.168.1.50", port: 9303))
        #expect(signaling.accepts.first?.peerSid == Self.consoleSid)
        // The ack goes out from a task of its own, so wait for the ack itself rather than assume it has landed.
        #expect(await eventually { signaling.results == [1] })
    }

    @Test("the timeline: push up, create, command, OFFER, our Init, ACCEPT, register, leave")
    func timeline() async throws {
        let socket = FakeWebSocket()
        let connectedAtCreate = Mutex(false)
        let signaling = FakeSignaling(onCreate: { connectedAtCreate.withLock { $0 = socket.isConnected } },
                                      onCommand: Self.console(socket, seed: Array(0..<16)))
        let transport = FakeAccountTransport(recordingInto: signaling)
        _ = try await pair(signaling, transport, socket)

        #expect(connectedAtCreate.withLock { $0 })
        #expect(signaling.timeline == [
            "create", "command",
            "offer sid=1 reqId=1",
            "open",
            "accept sid=1 reqId=2 peerSid=\(Self.consoleSid)",
            "register",
            "leave",
        ])
        #expect(transport.opened.count == 1)
    }

    @Test("our OFFER carries STUN, STATIC and LOCAL when a mapping is known")
    func offerCandidates() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: Self.console(socket, seed: Array(0..<16)))
        var request = self.request
        request.reflexiveEndpoint = UDPEndpoint(address: "198.51.100.202", port: 1298)
        _ = try await pair(signaling, FakeAccountTransport(recordingInto: signaling), socket, request: request)
        #expect(signaling.offers.first?.candidates.map(\.type) == ["STUN", "STATIC", "LOCAL"])
        #expect(signaling.accepts.first?.localAddress == "192.168.1.9")
        #expect(signaling.accepts.first?.localPort == 51000)
    }

    @Test("every console OFFER and ACCEPT is acked once, however often it arrives, and a RESULT is not")
    func acksOnce() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: { seeds in
            Self.console(socket, seed: Array(0..<16))(seeds)
            socket.push(Frames.accountOffer(hashedID: Self.consoleHashedID, sid: Self.consoleSid))   // the duplicate
            socket.push(Frames.consoleAccept(reqID: 14, sid: Self.consoleSid))
            socket.push(Frames.consoleAccept(reqID: 14, sid: Self.consoleSid))
            socket.push(Frames.signaling(from: "PROSPERO", #"{"action":"RESULT","reqId":1,"error":0,"connRequest":{}}"#))
        })
        _ = try await pair(signaling, FakeAccountTransport(recordingInto: signaling), socket)
        #expect(await eventually { signaling.results.sorted() == [1, 14] })
        try await Task.sleep(for: .milliseconds(50))
        #expect(signaling.results.count == 2)
    }

    @Test("times out cleanly when no seed is published, and still leaves")
    func noSeed() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling()
        let transport = FakeAccountTransport(recordingInto: signaling)
        var request = self.request
        request.localHashedID = []   // no join wait, so the seed is the first thing to time out
        do {
            _ = try await pair(signaling, transport, socket, request: request,
                               options: AccountRendezvousOptions(seedTimeout: .milliseconds(150)))
            Issue.record("expected a failure")
        } catch {
            #expect(String(describing: error).contains("customData1"))
        }
        #expect(transport.registered.isEmpty)
        #expect(signaling.calls.last == "leave")
    }

    @Test("when the console never joins, it says so rather than blaming the seed")
    func neverJoins() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling()
        do {
            _ = try await pair(signaling, FakeAccountTransport(recordingInto: signaling), socket,
                               options: AccountRendezvousOptions(seedTimeout: .milliseconds(150), offerTimeout: .milliseconds(150)))
            Issue.record("expected a failure")
        } catch {
            #expect(String(describing: error).contains("never joined"))
        }
    }

    @Test("a seed that arrives and will not open is reported as that, not as missing")
    func unreadableSeed() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: Self.console(socket, seed: [], offer: false, customData1: "bm90LWEtc2VlZA=="))
        do {
            _ = try await pair(signaling, FakeAccountTransport(recordingInto: signaling), socket,
                               options: AccountRendezvousOptions(seedTimeout: .milliseconds(150)))
            Issue.record("expected a failure")
        } catch {
            #expect(String(describing: error).contains("1 of 1 customData1 frames"))
        }
    }

    @Test("a console that never offers has no address to reach it at")
    func neverOffers() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: Self.console(socket, seed: Array(0..<16), offer: false))
        do {
            _ = try await pair(signaling, FakeAccountTransport(recordingInto: signaling), socket,
                               options: AccountRendezvousOptions(offerTimeout: .milliseconds(150)))
            Issue.record("expected a failure")
        } catch {
            #expect(String(describing: error).contains("never offered"))
        }
        #expect(!signaling.calls.contains { $0.hasPrefix("offer") })   // we never offer first
    }

    @Test("an association that will not open still gets the ACCEPT, and pairing then says why it stopped")
    func openFails() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: Self.console(socket, seed: Array(0..<16)))
        let transport = FakeAccountTransport(recordingInto: signaling, failOpen: true)
        do {
            _ = try await pair(signaling, transport, socket)
            Issue.record("expected a failure")
        } catch {
            #expect(String(describing: error).contains("never opened"))
        }
        #expect(signaling.timeline.contains { $0.hasPrefix("accept") })
        #expect(transport.registered.isEmpty)
    }

    @Test("a connect keeps the session until closed, and negotiates the media leg on the console's second OFFER")
    func connectAndMedia() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: Self.console(socket, seed: Array(0..<16)))
        let transport = FakeAccountTransport(recordingInto: signaling)
        let rendezvous = AccountRendezvous(signaling: signaling, transport: transport)
        let connection = try await rendezvous.connect(request, push: PushChannel(socket: socket), pushServer: server,
                                                      accessToken: "token", registerFirst: false)
        #expect(connection.registration == nil)
        #expect(!signaling.calls.contains("leave"))
        #expect(!signaling.calls.contains("register"))

        socket.push(Frames.accountOffer(hashedID: Self.consoleHashedID, sid: 30001, address: "192.168.1.51"))
        let media = try await connection.negotiateMedia(localPort: 52000, reflexive: nil)
        #expect(media == MediaEndpoint(address: "192.168.1.51", port: 9303, consoleHashedID: Self.consoleHashedID))
        #expect(signaling.offers.last == FakeSignaling.Offer(
            candidates: [SignalingCandidate(type: "LOCAL", address: "192.168.1.9", port: 52000)],
            localHashedID: Self.ourHashedID, reqID: 3, sid: 2))
        #expect(signaling.accepts.last?.reqID == 4)
        #expect(signaling.accepts.last?.sid == 2)
        #expect(signaling.accepts.last?.peerSid == 30001)
        #expect(signaling.accepts.last?.localPort == 52000)

        // The console's later messages are still acked: the subscription outlives the call.
        socket.push(Frames.consoleAccept(reqID: 22, sid: 30001))
        #expect(await eventually { signaling.results.contains(22) })

        await connection.lifetime.close()
        #expect(signaling.calls.last == "leave")
        await connection.lifetime.close()   // idempotent
        #expect(signaling.calls.filter { $0 == "leave" }.count == 1)
    }

    @Test("a connect that registers first waits for the seed and hands the record over")
    func connectRegistering() async throws {
        let socket = FakeWebSocket()
        let seed: [UInt8] = Array(200..<216)
        let signaling = FakeSignaling(onCommand: Self.console(socket, seed: seed))
        let transport = FakeAccountTransport(recordingInto: signaling)
        let connection = try await AccountRendezvous(signaling: signaling, transport: transport)
            .connect(request, push: PushChannel(socket: socket), pushServer: server, accessToken: "token", registerFirst: true)
        #expect(connection.registration == [1, 2, 3, 4])
        #expect(transport.registered == [seed])
        await connection.lifetime.close()
    }

    @Test("the media leg is nil when the console never offers one")
    func noMediaOffer() async throws {
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: Self.console(socket, seed: Array(0..<16)))
        let connection = try await AccountRendezvous(signaling: signaling, transport: FakeAccountTransport(recordingInto: signaling),
                                                     options: AccountRendezvousOptions(offerTimeout: .milliseconds(150)))
            .connect(request, push: PushChannel(socket: socket), pushServer: server, accessToken: "token", registerFirst: false)
        #expect(try await connection.negotiateMedia(localPort: 52000, reflexive: nil) == nil)
        await connection.lifetime.close()
    }
}

@Suite("Cloud: our candidates")
struct AccountCandidateTests {
    let local = UDPEndpoint(address: "10.0.0.7", port: 63711)
    typealias R = AccountRendezvous<FakeAccountTransport>

    @Test("STUN, STATIC and LOCAL, in that order")
    func allThree() {
        let candidates = R.ourCandidates(local: local, reflexive: UDPEndpoint(address: "198.51.100.202", port: 1298))
        #expect(candidates == [SignalingCandidate(type: "STUN", address: "198.51.100.202", port: 1298),
                               SignalingCandidate(type: "STATIC", address: "198.51.100.202", port: 63711),
                               SignalingCandidate(type: "LOCAL", address: "10.0.0.7", port: 63711)])
    }

    @Test("a port-preserving NAT does not produce two identical candidates")
    func portPreserving() {
        #expect(R.ourCandidates(local: local, reflexive: UDPEndpoint(address: "198.51.100.202", port: 63711)).map(\.type)
                == ["STUN", "LOCAL"])
    }

    @Test("with no reflexive address the local one still stands")
    func localOnly() {
        #expect(R.ourCandidates(local: local, reflexive: nil).map(\.type) == ["LOCAL"])
    }

    @Test("with nothing known, nothing is offered")
    func nothing() {
        #expect(R.ourCandidates(local: nil, reflexive: nil).isEmpty)
    }

    @Test("the console candidate: our own subnet first, then the first that parses, strict dotted quads only")
    func chosen() {
        let home: (String) -> Bool = { $0.hasPrefix("192.168.1.") }
        let offered = [
            SignalingCandidate(type: "LOCAL", address: "console.local", port: 9303),
            SignalingCandidate(type: "STATIC", address: "203.0.113.7", port: 9303),
            SignalingCandidate(type: "LOCAL", address: "192.168.1.50", port: 9303),
        ]
        #expect(ConsoleCandidates.choose(offered, sharesSubnet: home)?.address == "192.168.1.50")
        #expect(ConsoleCandidates.choose(offered, sharesSubnet: { _ in false })?.address == "203.0.113.7")
        for bad in ["10", "::1", "1.2.3", "256.1.1.1"] {
            #expect(ConsoleCandidates.choose([SignalingCandidate(type: "LOCAL", address: bad, port: 9303)],
                                             sharesSubnet: home) == nil, "\(bad)")
        }
    }

    /// The case that used to diverge: nothing parses. The ACCEPT named the unparseable candidate while the
    /// transport fell back to the known host; now both get the known host on 9303.
    @Test("with nothing parseable, the association and the ACCEPT fall back to the known host together")
    func fallback() {
        let unparseable = [SignalingCandidate(type: "STATIC", address: "console.local", port: 1234)]
        let named = ConsoleCandidates.resolve(unparseable, consoleHost: "192.168.1.20", fallbackPort: 9303,
                                              sharesSubnet: { _ in false })
        #expect(named == SignalingCandidate(type: "LOCAL", address: "192.168.1.20", port: 9303))
        let context = AccountTransportContext(
            consoleOffer: SignalingMessage(action: "OFFER", reqID: 1, fromPlatform: "PROSPERO", candidates: unparseable),
            consoleHost: "192.168.1.20", localHashedID: [], consoleHashedID: [], selectedCandidate: named)
        #expect(ConsolePath.control(context)?.text == named?.address)
        #expect(ConsolePath.control(context)?.port == 9303)

        #expect(ConsoleCandidates.resolve(unparseable, consoleHost: "", fallbackPort: 9303, sharesSubnet: { _ in false }) == nil)
    }
}
