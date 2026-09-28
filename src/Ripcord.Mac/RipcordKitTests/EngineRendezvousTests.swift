// The engine-backed rendezvous transports, against the engine's scripted 9303 console on loopback.
//
// RendezvousTests.swift pins the cloud timelines against a fake transport; this pins the transports those
// timelines drive, with everything below them real: the seed crypto against the engine's own seal, the
// 9303 association and /sess/rgst against the engine's scripted console (ripcord_loopback_dgram_console_*,
// test-support, which answers the account route's /sess/rgst under a seed), and ConsoleSession's rendezvous
// route through prepare, begin, registration and connect, as far as a console with no A/V leg lets it go.
//
// NOTHING LEAVES LOOPBACK. Every socket binds 127.0.0.1, every peer is 127.0.0.1, no STUN server is named,
// and the cloud is FakeSignaling and FakeWebSocket. Every id, key and account number is synthetic.

import CRipcordEngine
import Foundation
@testable import RipcordKit
import Synchronization
import Testing

/// The engine's scripted 9303 console, on its own thread in the engine, until finished.
final class LoopbackConsole: @unchecked Sendable {
    struct Report: Sendable {
        var rgstRequests: Int
        var rgstFieldOK: Bool
        var inits: Int
        var requests: [String]
    }

    let port: Int
    let consoleID: [UInt8]
    let clientID: [UInt8]
    private let console: OpaquePointer

    init(seed: [UInt8], isPS5: Bool = true) throws {
        let nonce = (0..<16).map { UInt8(0x10 + $0) }
        var port: UInt16 = 0
        guard let console = ripcord_loopback_dgram_console_start(isPS5, seed, nonce, &port) else {
            throw PairingError.associationFailed("the loopback console could not bind")
        }
        self.console = console
        self.port = Int(port)
        var cid = [UInt8](repeating: 0, count: 20), clid = [UInt8](repeating: 0, count: 20)
        _ = ripcord_loopback_dgram_console_ids(&cid, &clid)
        consoleID = cid
        clientID = clid
    }

    /// Reads what the console saw, after a moment for the client's last datagrams, and stops it.
    func finish() -> Report {
        usleep(50_000)
        var r = RipcordDgramConsoleReport()
        _ = ripcord_loopback_dgram_console_report(console, &r)
        let codes = withUnsafeBytes(of: r.requests) { Array($0.prefix(Int(min(r.request_count, 4)))) }
        let paths = codes.map { ["?", "/sess/rgst", "/sess/init", "/sess/ctrl"][Int(min($0, 3))] }
        return Report(rgstRequests: Int(r.rgst_requests), rgstFieldOK: r.rgst_field_ok, inits: Int(r.inits),
                      requests: paths)
    }

    deinit { ripcord_loopback_dgram_console_stop(console) }
}

/// customData1 as the console publishes it: the seed sealed under data1/data2, double base64.
func sealedCustomData1(seed: [UInt8], data1: [UInt8], data2: [UInt8], isPS5: Bool = true) -> String {
    var text = [UInt8](repeating: 0, count: 128)
    _ = ripcord_account_seed_seal(isPS5, data1, data2, seed, &text, text.count)
    return String(decoding: text.prefix(while: { $0 != 0 }), as: UTF8.self)
}

private let accountID = "1234567890123456"   // synthetic, the C suites' own

private func registrationKey(_ console: PairedConsole) -> [UInt8] { console.registrationKey }

@Suite("engine rendezvous transports", .serialized)
struct EngineRendezvousTests {
    // MARK: - The seed

    @Test("seed material is fresh, and a customData1 sealed by the engine opens to the console's seed")
    func seedRoundTrip() throws {
        let material = try AccountSeed.makeKeyMaterial()
        #expect(material.data1.count == 16 && material.data2.count == 16)
        #expect(material.data1 != material.data2)
        #expect(try AccountSeed.makeKeyMaterial() != material)

        let seed: [UInt8] = Array(200..<216)
        let wire = sealedCustomData1(seed: seed, data1: material.data1, data2: material.data2)
        #expect(!wire.isEmpty)
        #expect(AccountSeed.recover(customData1: wire, keyMaterial: material, family: .ps5) == seed)

        // The transports answer the protocol with exactly this.
        let transport = try DatagramAccountTransport(family: .ps5, accountID: accountID,
                                                     options: .init(bindAddress: "127.0.0.1"))
        #expect(transport.recoverSeed(customData1: wire, keyMaterial: material, family: .ps5) == seed)
    }

    @Test("a wrong key opens to noise rather than nil; only malformed input is nil")
    func seedEdges() throws {
        let material = try AccountSeed.makeKeyMaterial()
        let seed: [UInt8] = Array(1...16)
        let wire = sealedCustomData1(seed: seed, data1: material.data1, data2: material.data2)

        let other = try AccountSeed.makeKeyMaterial()
        let noise = AccountSeed.recover(customData1: wire, keyMaterial: other, family: .ps5)
        #expect(noise != nil && noise != seed)

        #expect(AccountSeed.recover(customData1: "not base64 at all", keyMaterial: material, family: .ps5) == nil)
        #expect(AccountSeed.recover(customData1: "", keyMaterial: material, family: .ps5) == nil)
        #expect(AccountSeed.recover(customData1: wire, keyMaterial: SeedKeyMaterial(data1: [1], data2: [2]), family: .ps5) == nil)
    }

    // MARK: - Account pairing over its own leg

    @Test("account pairing, end to end: the rendezvous, our Init, /sess/rgst over 9303, a stored record")
    func pairsOverTheAssociation() async throws {
        let seed: [UInt8] = Array(0x40..<0x50)
        let console = try LoopbackConsole(seed: seed)
        let socket = FakeWebSocket()
        let signaling = FakeSignaling(onCommand: { seeds in
            socket.push(Frames.joined())
            let data1 = Array(Data(base64Encoded: seeds.data1) ?? Data())
            let data2 = Array(Data(base64Encoded: seeds.data2) ?? Data())
            socket.push(Frames.customData1(sealedCustomData1(seed: seed, data1: data1, data2: data2)))
            socket.push(Frames.accountOffer(hashedID: console.consoleID, sid: 24043, address: "127.0.0.1", port: console.port))
        })
        let transport = try DatagramAccountTransport(family: .ps5, accountID: accountID, consoleName: "Loopback console",
                                                     options: .init(bindAddress: "127.0.0.1", stageTimeout: .seconds(5),
                                                                    receiveTimeout: .milliseconds(200)))
        #expect(transport.localPort != 0)
        let request = AccountRendezvousRequest(consoleID: "c", consoleHost: "", consoleDUID: "duid-1", accountID: accountID,
                                               clientDeviceID: [], localHashedID: console.clientID,
                                               localEndpoint: UDPEndpoint(address: "127.0.0.1", port: transport.localPort))
        let rendezvous = AccountRendezvous(signaling: signaling, transport: transport)

        let paired = try await withTimeout(.seconds(20)) {
            try await rendezvous.pair(request, push: PushChannel(socket: socket), pushServer: PushServerInfo(fqdn: "host"),
                                      accessToken: "token")
        }
        let report = console.finish()

        #expect(report.rgstRequests == 1)
        #expect(report.rgstFieldOK)                        // the console read our field under the seed key
        #expect(report.inits >= 1)                         // our Init reached it
        #expect(paired.host == "127.0.0.1")                // the console's LOCAL candidate: no host was given
        #expect(paired.name == "Loopback console")
        #expect(paired.family == .ps5)
        #expect(registrationKey(paired) == Array("1a2b3c4d".utf8))
        #expect(paired.accountID == accountID)
        #expect(signaling.accepts.first?.peerSid == 24043)
        #expect(signaling.timeline.last == "leave")

        // The record is the one the LAN path reads: through the lab's store and back.
        let directory = FileManager.default.temporaryDirectory.appending(path: "ripcord-test-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = PairingFileStore(directory: directory)
        try store.save(paired)
        let loaded = try #require(store.load().first)
        #expect(loaded.host == "127.0.0.1" && loaded.name == "Loopback console" && loaded.family == .ps5)
        #expect(registrationKey(loaded) == Array("1a2b3c4d".utf8))
    }

    @Test("a console that never answers is a transport failure within the stage deadline, not a hang")
    func silentConsole() async throws {
        // A socket that is bound and never read: a console that is there and says nothing.
        let silent = socket(AF_INET, SOCK_DGRAM, 0)
        defer { close(silent) }
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = UInt32(0x7F00_0001).bigEndian
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let bound = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(silent, $0, length) == 0 && getsockname(silent, $0, &length) == 0
            }
        }
        try #require(bound)

        let transport = try DatagramAccountTransport(family: .ps5, accountID: accountID,
                                                     options: .init(bindAddress: "127.0.0.1", stageTimeout: .milliseconds(400),
                                                                    receiveTimeout: .milliseconds(100)))
        let candidate = SignalingCandidate(type: "LOCAL", address: "127.0.0.1", port: Int(UInt16(bigEndian: address.sin_port)))
        let context = AccountTransportContext(
            consoleOffer: SignalingMessage(action: "OFFER", reqID: 1, fromPlatform: "PROSPERO", candidates: [candidate]),
            consoleHost: "", localHashedID: Array(repeating: 1, count: 20), consoleHashedID: Array(repeating: 2, count: 20),
            selectedCandidate: candidate)

        try await transport.openAssociation(context)       // the Init only goes out; nothing is awaited
        let started = ContinuousClock.now
        await #expect(throws: PairingError.self) { _ = try await transport.register(context: context, seed: Array(repeating: 1, count: 16)) }
        #expect(ContinuousClock.now - started < .seconds(5))
    }

    @Test("the association refuses ids that are not 20 bytes, and registering before opening")
    func associationRefusals() async throws {
        let transport = try DatagramAccountTransport(family: .ps5, accountID: accountID, options: .init(bindAddress: "127.0.0.1"))
        let candidate = SignalingCandidate(type: "LOCAL", address: "127.0.0.1", port: 9)
        let offer = SignalingMessage(action: "OFFER", reqID: 1, fromPlatform: "PROSPERO", candidates: [candidate])
        let short = AccountTransportContext(consoleOffer: offer, consoleHost: "", localHashedID: Array(repeating: 1, count: 19),
                                            consoleHashedID: Array(repeating: 2, count: 20), selectedCandidate: candidate)
        await #expect(throws: PairingError.self) { try await transport.openAssociation(short) }
        await #expect(throws: PairingError.self) { _ = try await transport.register(context: short, seed: Array(repeating: 0, count: 16)) }

        let unaddressable = AccountTransportContext(consoleOffer: offer, consoleHost: "console.local",
                                                    localHashedID: Array(repeating: 1, count: 20),
                                                    consoleHashedID: Array(repeating: 2, count: 20),
                                                    selectedCandidate: SignalingCandidate(type: "LOCAL", address: "::1", port: 9303))
        await #expect(throws: PairingError.self) { try await transport.openAssociation(unaddressable) }
        #expect(throws: PairingError.self) { _ = try DatagramAccountTransport(family: .ps5, accountID: accountID,
                                                                              options: .init(bindAddress: "not-an-address")) }
    }

    // MARK: - ConsoleSession's rendezvous route

    /// A record the session can open with: synthetic key, host 127.0.0.1.
    static func loopbackRecord() -> PairedConsole {
        PairedConsole(host: "127.0.0.1", name: "Loopback console", consoleID: "", accountID: accountID, family: .ps5,
                      registrationKey: Array("1a2b3c4d".utf8), companion: (0..<16).map { UInt8(0xC0 + $0) },
                      deviceID: (0..<16).map { UInt8(0x30 + $0) })
    }

    static func route(_ drive: @escaping @Sendable (RendezvousLink) async throws -> Void) -> RendezvousRoute {
        var route = RendezvousRoute(stunServers: [], bindAddress: "127.0.0.1", drive: drive)
        route.dgramStageTimeout = .seconds(3)
        route.dgramReceiveTimeout = .milliseconds(100)
        route.mediaOfferTimeout = .seconds(3)
        route.driveTimeout = .seconds(20)
        return route
    }

    static func run(_ route: RendezvousRoute, cancelAfter: Duration? = nil) async throws -> SessionOutcome {
        let ended = OneShot<SessionOutcome>()
        var handlers = SessionHandlers()
        handlers.ended = { ended.succeed($0) }
        var options = ConsoleSession.Options()
        options.route = .rendezvous(route)
        let session = ConsoleSession(console: loopbackRecord(), options: options, handlers: handlers)
        session.start()
        if let cancelAfter {
            try await Task.sleep(for: cancelAfter)
            session.cancel()
        }
        return try await withTimeout(.seconds(40)) { try await ended.value() }
    }

    @Test("the route: prepare, begin, /sess/rgst, then /sess/init and /sess/ctrl on the same association, then poll_media")
    func sessionRoute() async throws {
        let seed: [UInt8] = Array(0x70..<0x80)
        let console = try LoopbackConsole(seed: seed)
        let registered = Mutex<PairedConsole?>(nil)
        let legs = Mutex<(control: RendezvousLeg?, media: RendezvousLeg?)>((nil, nil))
        let leftCloud = Mutex(false)

        let outcome = try await Self.run(Self.route { link in
            legs.withLock { $0.control = link.controlLeg }
            let candidate = SignalingCandidate(type: "LOCAL", address: "127.0.0.1", port: console.port)
            let context = AccountTransportContext(
                consoleOffer: SignalingMessage(action: "OFFER", reqID: 1, fromPlatform: "PROSPERO", candidates: [candidate],
                                               localHashedID: console.consoleID, sid: 7),
                consoleHost: "127.0.0.1", localHashedID: console.clientID, consoleHashedID: console.consoleID,
                selectedCandidate: candidate)
            let transport = link.accountTransport(family: .ps5, accountID: accountID)
            try await transport.openAssociation(context)
            let record = try await transport.register(context: context, seed: seed)
            registered.withLock { $0 = record }
            link.proceed(media: { leg in
                legs.withLock { $0.media = leg }
                return nil          // no A/V leg offered: the session ends at poll_media
            }, onEnd: {
                leftCloud.withLock { $0 = true }
            })
        })
        let report = console.finish()

        #expect(outcome.endReason == .noMedia)
        #expect(outcome.failure == nil)
        #expect(outcome.stage >= .controlOpen)
        #expect(report.requests == ["/sess/rgst", "/sess/init", "/sess/ctrl"])
        #expect(report.rgstFieldOK)
        let record = try #require(registered.withLock { $0 })
        #expect(record.host == "127.0.0.1" && registrationKey(record) == Array("1a2b3c4d".utf8))
        let (control, media) = legs.withLock { $0 }
        #expect((control?.localPort ?? 0) != 0 && control?.reflexive == nil)
        #expect((media?.localPort ?? 0) != 0 && media?.localPort != control?.localPort)
        #expect(leftCloud.withLock { $0 })                 // after the sockets closed, before `ended`
    }

    @Test("a cloud half that fails ends the session before connect, saying why, and nothing is sent")
    func driveFails() async throws {
        let outcome = try await Self.run(Self.route { _ in throw CloudError.rendezvous("the console never offered") })
        #expect(outcome.endReason == .rendezvousFailed)
        #expect(outcome.failure?.contains("the console never offered") == true)
        #expect(outcome.stage == .idle)
    }

    @Test("a cloud half that returns without proceeding is a failure too")
    func driveReturns() async throws {
        let outcome = try await Self.run(Self.route { _ in })
        #expect(outcome.endReason == .rendezvousFailed)
        #expect(outcome.failure != nil)
    }

    @Test("cancelling while the cloud half runs ends the session and cancels the cloud half")
    func cancelDuringDrive() async throws {
        let cancelled = Mutex(false)
        let outcome = try await Self.run(Self.route { _ in
            do { try await Task.sleep(for: .seconds(60)) } catch { cancelled.withLock { $0 = true }; throw error }
        }, cancelAfter: .milliseconds(200))
        #expect(outcome.endReason == .hostCancel)
        #expect(cancelled.withLock { $0 })
    }

    // MARK: - Route selection

    @Test("choosing a route leaves the LAN configuration exactly as it was")
    func routeSelection() {
        var options = ConsoleSession.Options()
        let lan = ConsoleSession.makeConfig(options)
        #expect(lan.route == UInt32(RIPCORD_ROUTE_LOCAL))
        #expect(lan.width == 1920 && lan.height == 1080 && lan.fps == 60 && lan.bitrate_kbps == 25_000)
        #expect(lan.allow_hevc && !lan.hdr && lan.require_session_ready == 0)
        #expect(lan.registration_key == nil && lan.stun_servers == nil && lan.stun_server_count == 0)
        #expect(lan.bind_address == (0, 0, 0, 0))
        #expect(lan.control_local_port == 0 && lan.media_local_port == 0)
        #expect(lan.media_offer_timeout_ms == 0 && lan.dgram_stage_timeout_ms == 0 && lan.dgram_receive_timeout_ms == 0)
        #expect(lan.signin_prompt_window_ms == 0 && lan.senkusha_attempts == 0 && lan.stream_attempts == 0)

        var route = RendezvousRoute(stunServers: [], drive: { _ in })
        route.controlLocalPort = 50_001
        route.mediaLocalPort = 50_002
        route.mediaOfferTimeout = .seconds(12)
        route.dgramStageTimeout = .milliseconds(2500)
        options.route = .rendezvous(route)
        let rendezvous = ConsoleSession.makeConfig(options)
        #expect(rendezvous.route == UInt32(RIPCORD_ROUTE_RENDEZVOUS))
        #expect(rendezvous.control_local_port == 50_001 && rendezvous.media_local_port == 50_002)
        #expect(rendezvous.media_offer_timeout_ms == 12_000 && rendezvous.dgram_stage_timeout_ms == 2_500)
        // Everything the LAN route reads is the same on both.
        #expect(rendezvous.width == lan.width && rendezvous.height == lan.height && rendezvous.fps == lan.fps)
        #expect(rendezvous.bitrate_kbps == lan.bitrate_kbps && rendezvous.allow_hevc == lan.allow_hevc)
        #expect(rendezvous.hdr == lan.hdr && rendezvous.require_session_ready == lan.require_session_ready)
    }

    @Test("STUN servers resolve to IPv4 in network order (numeric here, so no lookup is made)")
    func stunResolution() {
        let resolved = StunServer.resolve([StunServer(host: "127.0.0.1", port: 3478), StunServer(host: "127.0.0.2", port: 19302)])
        #expect(resolved.count == 2)
        #expect(resolved.first?.port == 3478)
        #expect(resolved.first.map { [$0.address.0, $0.address.1, $0.address.2, $0.address.3] } == [127, 0, 0, 1])
        #expect(StunServer.resolve(Array(repeating: StunServer(host: "127.0.0.1", port: 1), count: 9)).count == 4)
        #expect(StunServer.defaults.count == 3)
    }

    @Test("a media answer becomes the peer the engine aims the A/V leg at; an unusable one gives up")
    func mediaPeer() throws {
        let peer = try #require(ConsoleSession.peer(address: "127.0.0.1", port: 9297, hashedID: Array(1...20)))
        #expect(peer.endpoint.port == 9297)
        #expect(withUnsafeBytes(of: peer.endpoint.address) { Array($0) } == [127, 0, 0, 1])
        #expect(withUnsafeBytes(of: peer.console_hashed_id) { Array($0) } == Array(1...20))
        #expect(ConsoleSession.peer(address: "::1", port: 9297, hashedID: Array(1...20)) == nil)
        #expect(ConsoleSession.peer(address: "127.0.0.1", port: 0, hashedID: Array(1...20)) == nil)
        #expect(ConsoleSession.peer(address: "127.0.0.1", port: 9297, hashedID: Array(1...19)) == nil)
    }
}
