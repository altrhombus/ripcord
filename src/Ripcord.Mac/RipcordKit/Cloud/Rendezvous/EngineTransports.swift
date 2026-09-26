// The engine behind RendezvousTransport.swift's two protocols: AccountRouteTransport and
// WanRendezvousTransport.
//
// TWO ACCOUNT TRANSPORTS, BECAUSE THERE ARE TWO OWNERS OF THE SOCKET.
//
//   DatagramAccountTransport   account pairing. No stream follows, so its client exists only to register:
//                              a rendezvous client with no registration key yet, whose control leg is
//                              prepared at init, aimed and begun by openAssociation, and registered over by
//                              ripcord_client_rendezvous_register. It is never connected.
//                              HalyardAccountConsolePairing's HalyardDatagramRegistrationTransport.
//   SessionAccountTransport    an account-route connect. The association is the one the stream will run on,
//                              so it belongs to ConsoleSession's client, and this transport only asks the
//                              session thread to act on it. HalyardAccountConsoleSession's
//                              transport-plus-registerFirst.
//
// Both register into a PairedConsole, so an account pairing ends in exactly what the LAN path already uses.
//
// THREADING. A client handle is used from one thread at a time (ripcord.h). DatagramAccountTransport runs
// every call on one serial queue; SessionAccountTransport runs nothing itself, the session thread does.

internal import CRipcordEngine
import Darwin
import Dispatch
import Foundation
import Synchronization

// MARK: - The seed

public enum AccountSeed {
    /// Fresh data1 (the seed-delivery field key) and data2 (its material) for one connect command: two
    /// independent draws from the platform CSPRNG.
    public static func makeKeyMaterial() throws -> SeedKeyMaterial {
        var data1 = [UInt8](repeating: 0, count: 16)
        var data2 = [UInt8](repeating: 0, count: 16)
        var random = EngineRandom.table
        guard ripcord_account_key_material(&random, &data1, &data2) == RIPCORD_STATUS_OK else {
            throw CloudError.rendezvous("This Mac could not produce random bytes for the registration seed.")
        }
        return SeedKeyMaterial(data1: data1, data2: data2)
    }

    /// The 16-byte seed from customData1 as the push channel delivered it (double base64), or nil when it
    /// does not decode or is shorter than a seed.
    ///
    /// **A wrong key is not detectable here.** The field cipher has no tag, so a customData1 sealed under
    /// someone else's data1/data2 opens to sixteen bytes of noise, not to nil. Nil therefore means
    /// "malformed", and a foreign-but-well-formed frame is only caught later, as a registration the console
    /// refuses to decrypt (a bad record).
    public static func recover(customData1: String, keyMaterial: SeedKeyMaterial, family: ConsoleFamily) -> [UInt8]? {
        guard keyMaterial.data1.count == 16, keyMaterial.data2.count == 16 else { return nil }
        var seed = [UInt8](repeating: 0, count: 16)
        let text = Array(customData1.utf8)
        let status = ripcord_account_seed_recover(family == .ps5, keyMaterial.data1, keyMaterial.data2, text, text.count,
                                                  &seed)
        return status == RIPCORD_STATUS_OK ? seed : nil
    }
}

// MARK: - /sess/rgst over a client's control leg

enum AccountRegistration {
    /// Registers over `client`'s control leg, between begin and connect. The record's secrets are wiped from
    /// the C struct before returning; the PairedConsole carries its own copy.
    static func run(_ client: OpaquePointer, family: ConsoleFamily, accountID: String, clientIP: String, seed: [UInt8],
                    host: String, name: String, consoleID: String) throws(PairingError) -> PairedConsole {
        guard seed.count == 16 else { throw .associationFailed("the registration seed is not 16 bytes") }
        let account = Pairing.normalisedAccountID(accountID)
        var result = RipcordRegistResult()
        let status = ripcord_client_rendezvous_register(client, family == .ps5, seed, account, clientIP, &result)
        defer { EngineRecords.wipe(&result) }
        guard status == RIPCORD_STATUS_OK, result.status == UInt32(RIPCORD_REGIST_OK) else {
            throw .accountRegistrationFailed(status: EngineRecords.statusText(result.status),
                                             httpStatus: Int(result.http_status), consoleReason: EngineRecords.reason(result))
        }
        return EngineRecords.console(result.record, host: host, name: name, consoleID: consoleID, accountID: account)
    }
}

// MARK: - Where the 9303 association is aimed

/// An IPv4 endpoint as the C side takes one: address bytes in network order, port in host order.
struct ConsolePath: Sendable, Equatable {
    var address: [UInt8]
    var port: UInt16
    var text: String

    /// The strict dotted quad a console sends, and nothing else: four decimal parts of one to three digits,
    /// each at most 255 (the engine's `candidates::parse_ipv4`). A candidate address arrives from the
    /// network by way of the cloud, so the looser forms inet_pton would take are not read.
    static func ipv4(_ text: String) -> [UInt8]? {
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var out: [UInt8] = []
        for part in parts {
            guard (1...3).contains(part.count), part.allSatisfy({ $0.isASCII && $0.isNumber }),
                  let value = UInt16(part), value <= 255 else { return nil }
            out.append(UInt8(value))
        }
        return out
    }

    init?(address text: String, port: Int) {
        guard let bytes = Self.ipv4(text), (1...65_535).contains(port) else { return nil }
        address = bytes
        self.port = UInt16(port)
        self.text = text
    }

    /// The control association's endpoint: the candidate our ACCEPT names (the transport and the ACCEPT
    /// must agree, RendezvousTransport.swift), else the host we already had, on 9303. .NET's
    /// HalyardAccountConsoleSession.ConsoleEndpoint, and the fallback the engine's candidate choice leaves
    /// to its caller.
    static func control(_ context: AccountTransportContext) -> ConsolePath? {
        if let candidate = context.selectedCandidate, let path = ConsolePath(address: candidate.address, port: candidate.port) {
            return path
        }
        return ConsolePath(address: context.consoleHost, port: 9303)
    }

    /// The host a pairing record should carry: the address the console is reached at on its own network.
    /// The caller's host when it had one (LAN discovery found it); else the console's own LOCAL candidate;
    /// else wherever the association went.
    static func recordHost(_ context: AccountTransportContext, path: ConsolePath) -> String {
        if !context.consoleHost.isEmpty { return context.consoleHost }
        if let local = context.consoleOffer.candidates.first(where: { $0.type == "LOCAL" && ipv4($0.address) != nil }) {
            return local.address
        }
        return path.text
    }
}

func validateHashedIDs(_ context: AccountTransportContext) throws(PairingError) {
    let length = 20
    guard context.localHashedID.count == length else { throw .associationFailed("our localHashedId is not \(length) bytes") }
    guard context.consoleHashedID.count == length else {
        throw .associationFailed("the console's OFFER carried no \(length)-byte localHashedId")
    }
}

// MARK: - Account pairing: a client of its own

/// Account ("no PIN") pairing's transport: a rendezvous client that only ever registers. Its control leg is
/// bound at init, so its port is known before the OFFER that advertises it.
public final class DatagramAccountTransport: AccountRouteTransport {
    public typealias Registration = PairedConsole

    public struct Options: Sendable {
        /// The address to bind (dotted quad). Nil binds every interface; a test binds 127.0.0.1.
        public var bindAddress: String?
        /// 0 lets the system choose.
        public var localPort: UInt16
        /// Each 9303 stage's deadline, and the quiet window before a re-send. Zero takes the engine's defaults
        /// (30 s and 5 s, HalyardDatagramControlOptions').
        public var stageTimeout: Duration
        public var receiveTimeout: Duration
        public var log: (@Sendable (String) -> Void)?

        public init(bindAddress: String? = nil, localPort: UInt16 = 0, stageTimeout: Duration = .zero,
                    receiveTimeout: Duration = .zero, log: (@Sendable (String) -> Void)? = nil) {
            self.bindAddress = bindAddress
            self.localPort = localPort
            self.stageTimeout = stageTimeout
            self.receiveTimeout = receiveTimeout
            self.log = log
        }
    }

    public let family: ConsoleFamily
    public let accountID: String
    public let consoleName: String
    public let consoleID: String
    /// The bound socket's port: our LOCAL candidate's, and the `mappedPort` of our ACCEPT.
    public let localPort: Int

    private let options: Options
    private let queue = DispatchQueue(label: "Ripcord account association")
    private let box: ClientBox

    /// Binds the control leg. Throws when it cannot be bound.
    public init(family: ConsoleFamily, accountID: String, consoleName: String = "", consoleID: String = "",
                options: Options = Options()) throws(PairingError) {
        self.family = family
        self.accountID = accountID
        self.consoleName = consoleName
        self.consoleID = consoleID
        self.options = options

        var config = RipcordClientConfig()
        config.route = UInt32(RIPCORD_ROUTE_RENDEZVOUS)
        config.is_ps5 = family == .ps5
        config.control_local_port = options.localPort
        config.dgram_stage_timeout_ms = options.stageTimeout.clampedMilliseconds
        config.dgram_receive_timeout_ms = options.receiveTimeout.clampedMilliseconds
        if let address = options.bindAddress {
            guard let parsed = ConsolePath.ipv4(address) else { throw .associationFailed("\(address) is not an IPv4 address to bind") }
            config.bind_address = (parsed[0], parsed[1], parsed[2], parsed[3])
        }
        guard let box = ClientBox(config: config, log: options.log) else {
            throw .associationFailed("could not create the registration client")
        }
        var leg = RipcordLeg()
        guard ripcord_client_rendezvous_prepare(box.client, &leg) == RIPCORD_STATUS_OK else {
            throw .associationFailed("could not bind a UDP socket for the control association")
        }
        self.box = box
        self.localPort = Int(leg.local_port)
    }

    public func makeSeedKeyMaterial() throws -> SeedKeyMaterial { try AccountSeed.makeKeyMaterial() }

    public func recoverSeed(customData1: String, keyMaterial: SeedKeyMaterial, family: ConsoleFamily) -> [UInt8]? {
        AccountSeed.recover(customData1: customData1, keyMaterial: keyMaterial, family: family)
    }

    /// Our Init: aim the leg at the console and put the Init on the wire, without waiting for an answer
    /// (.NET's PrepareAsync). The establishment happens in `register`.
    public func openAssociation(_ context: AccountTransportContext) async throws {
        let log = options.log, localPort = self.localPort
        try await onQueue { box throws(PairingError) in
            try validateHashedIDs(context)
            guard let path = ConsolePath.control(context) else {
                throw .associationFailed("the console offered no IPv4 candidate, and there is no host to fall back to")
            }
            var peer = RipcordPeer.make(path: path, consoleHashedID: context.consoleHashedID)
            guard ripcord_client_rendezvous_begin(box.client, context.localHashedID, &peer) == RIPCORD_STATUS_OK else {
                throw .associationFailed("our Init")
            }
            box.path = path
            log?("control association begun toward \(path.text):\(path.port) from local port \(localPort)")
        }
    }

    /// /sess/rgst over the association: establishes it, opens a connection, and exchanges.
    public func register(context: AccountTransportContext, seed: [UInt8]) async throws -> PairedConsole {
        let family = self.family, accountID = self.accountID, name = consoleName, consoleID = self.consoleID
        return try await onQueue { box throws(PairingError) in
            guard let path = box.path else { throw .associationFailed("the association was never opened") }
            let clientIP = try Pairing.localAddress(toward: path.text)
            return try AccountRegistration.run(box.client, family: family, accountID: accountID, clientIP: clientIP,
                                               seed: seed, host: ConsolePath.recordHost(context, path: path),
                                               name: name, consoleID: consoleID)
        }
    }

    private func onQueue<T: Sendable>(_ work: @escaping @Sendable (ClientBox) throws(PairingError) -> T) async throws -> T {
        let box = self.box
        return try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do { continuation.resume(returning: try work(box)) } catch { continuation.resume(throwing: error) }
            }
        }
    }
}

/// The registration client, touched only on the transport's serial queue, or in deinit when nothing else
/// can reach it. Freeing it closes the leg politely.
final class ClientBox: @unchecked Sendable {
    let client: OpaquePointer
    var path: ConsolePath?
    private let sink: Unmanaged<LogSink>?

    init?(config: RipcordClientConfig, log: (@Sendable (String) -> Void)?) {
        var config = config
        let sink = log.map { Unmanaged.passRetained(LogSink($0)) }
        var callbacks = RipcordClientCallbacks()
        callbacks.user = sink?.toOpaque()
        callbacks.video_frame = { _, _, _, _ in }
        callbacks.poll_media = { _, _, _ in -1 }
        if sink != nil {
            callbacks.log = { user, _, line in
                guard let user, let line else { return }
                Unmanaged<LogSink>.fromOpaque(user).takeUnretainedValue().log("9303: " + String(cString: line))
            }
        }
        var random = EngineRandom.table
        var ecdh = CryptoKitEngineECDH.backend
        guard let client = ripcord_client_new(&config, &callbacks, &ecdh, &random) else {
            sink?.release()
            return nil
        }
        self.client = client
        self.sink = sink
    }

    deinit {
        ripcord_client_free(client)
        sink?.release()
    }
}

final class LogSink: Sendable {
    let log: @Sendable (String) -> Void
    init(_ log: @escaping @Sendable (String) -> Void) { self.log = log }
}

extension RipcordPeer {
    static func make(path: ConsolePath, consoleHashedID: [UInt8]) -> RipcordPeer {
        var peer = RipcordPeer()
        peer.endpoint = RipcordEndpoint(address: (path.address[0], path.address[1], path.address[2], path.address[3]),
                                        port: path.port)
        withUnsafeMutableBytes(of: &peer.console_hashed_id) { $0.copyBytes(from: consoleHashedID.prefix(20)) }
        return peer
    }
}

// MARK: - An account-route connect: the session's own association

/// The account route's transport for a connect: the association is ConsoleSession's, so every step runs on
/// the session thread through its RendezvousLink. Built with `RendezvousLink.accountTransport`.
public struct SessionAccountTransport: AccountRouteTransport {
    public typealias Registration = PairedConsole

    let link: RendezvousLink
    public let family: ConsoleFamily
    public let accountID: String

    public func makeSeedKeyMaterial() throws -> SeedKeyMaterial { try AccountSeed.makeKeyMaterial() }

    public func recoverSeed(customData1: String, keyMaterial: SeedKeyMaterial, family: ConsoleFamily) -> [UInt8]? {
        AccountSeed.recover(customData1: customData1, keyMaterial: keyMaterial, family: family)
    }

    /// ripcord_client_rendezvous_begin, on the session thread: step 5 of the route.
    public func openAssociation(_ context: AccountTransportContext) async throws {
        try await link.begin(context)
    }

    /// Step 5a: ripcord_client_rendezvous_register, on the session thread.
    /// The record is returned for the caller to log or keep; .NET's connect does not store it.
    public func register(context: AccountTransportContext, seed: [UInt8]) async throws -> PairedConsole {
        try await link.register(context: context, seed: seed, family: family, accountID: accountID)
    }
}

// MARK: - The WAN rendezvous, over the session's prepared leg

/// WanRendezvous's transport over the control leg ripcord_client_rendezvous_prepare bound and asked STUN
/// about: the port it advertises and the mapping it offers are that socket's, as a NAT binding is per port.
struct LegWanTransport: WanRendezvousTransport {
    let leg: RendezvousLeg
    var localPort: Int { leg.localPort }
    func gatherReflexive() async -> UDPEndpoint? { leg.reflexive }
}

extension Duration {
    /// Whole milliseconds, clamped to what a C `uint32_t` deadline holds.
    var clampedMilliseconds: UInt32 {
        let (seconds, attoseconds) = components
        let ms = seconds.multipliedReportingOverflow(by: 1000)
        guard !ms.overflow else { return seconds > 0 ? UInt32.max : 0 }
        return UInt32(clamping: max(0, ms.partialValue + attoseconds / 1_000_000_000_000_000))
    }
}
