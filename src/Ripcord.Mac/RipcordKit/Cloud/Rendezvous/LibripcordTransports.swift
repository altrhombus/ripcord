// The C side of RendezvousTransport.swift's two protocols: libripcord behind AccountRouteTransport and
// WanRendezvousTransport, which until now only had fakes.
//
// TWO ACCOUNT TRANSPORTS, BECAUSE THERE ARE TWO OWNERS OF THE SOCKET.
//
//   DatagramAccountTransport   account pairing. No stream follows, so there is no halyard_client: the
//                              transport owns one rendezvous leg (session/halyard_dgram_session.h) - the
//                              socket, then halyard_rendezvous_leg_attach + halyard_dgram_channel_begin for our
//                              Init, then halyard_account_regist_run over halyard_dgram_regist_exchange.
//                              HalyardAccountConsolePairing's HalyardDatagramRegistrationTransport.
//   SessionAccountTransport    an account-route connect. The association is the one the stream will run on,
//                              so it belongs to ConsoleSession's halyard_client, and this transport only asks
//                              the session thread to act on it (halyard_client_rendezvous_begin, and
//                              halyard_account_regist_run over halyard_client_rendezvous_exchange).
//                              HalyardAccountConsoleSession's transport-plus-registerFirst.
//
// Both register into a PairedConsole: the record format Pairing.swift stores and PairingStore reads, so an
// account pairing ends in exactly what the LAN path already uses.
//
// THREADING. The C here is blocking and not reentrant. DatagramAccountTransport runs every call for its leg
// on one serial queue (the leg is never touched from two threads at once, and always with a happens-before
// between calls); SessionAccountTransport runs nothing itself, the session thread does. /sess/rgst's request
// and reply buffers are file-scope statics in halyard_account_regist_flow.c, so every registration in the
// process goes through AccountRegistration.run, one at a time.

internal import CLibripcord
import Darwin
import Dispatch
import Foundation
import Synchronization

// MARK: - The seed (halyard_account_regist_flow.h, halyard/halyard_account_seed.h)

public enum AccountSeed {
    /// Fresh data1 (the seed-delivery field key) and data2 (its material) for one connect command:
    /// `halyard_account_regist_generate_key_material`, from the platform CSPRNG.
    public static func makeKeyMaterial() throws -> SeedKeyMaterial {
        var data1 = [UInt8](repeating: 0, count: 16)
        var data2 = [UInt8](repeating: 0, count: 16)
        guard halyard_account_regist_generate_key_material(&data1, &data2) == 1 else {
            throw CloudError.rendezvous("This Mac could not produce random bytes for the registration seed.")
        }
        return SeedKeyMaterial(data1: data1, data2: data2)
    }

    /// The 16-byte seed from customData1 as the push channel delivered it (double base64), or nil when it
    /// does not decode or is shorter than a seed: `halyard_account_seed_recover_custom_data1`.
    ///
    /// **A wrong key is not detectable here.** The field cipher has no tag, so a customData1 sealed under
    /// someone else's data1/data2 opens to sixteen bytes of noise, not to nil (halyard_account_seed.h). Nil
    /// therefore means "malformed", and a foreign-but-well-formed frame is only caught later, as a
    /// registration the console refuses to decrypt (a bad record).
    public static func recover(customData1: String, keyMaterial: SeedKeyMaterial, family: ConsoleFamily) -> [UInt8]? {
        guard keyMaterial.data1.count == 16, keyMaterial.data2.count == 16 else { return nil }
        var seed = [UInt8](repeating: 0, count: Int(HALYARD_ACCOUNT_SEED_LENGTH))
        let ok = halyard_account_seed_recover_custom_data1(family == .ps5 ? 1 : 0, keyMaterial.data1, keyMaterial.data2,
                                                           customData1, customData1.utf8.count, &seed)
        return ok == 1 ? seed : nil
    }
}

// MARK: - /sess/rgst, one at a time

enum AccountRegistration {
    /// halyard_account_regist_run is not reentrant (its buffers are statics), so the process runs one.
    private static let serial = Mutex(())

    /// Builds the request from fresh randomness, sends it through `exchange` and opens the reply. The seed
    /// and the reply's key are wiped from the C structs before returning; the record carries its own copy.
    static func run(family: ConsoleFamily, accountID: String, clientIP: String, seed: [UInt8],
                    exchange: halyard_account_regist_exchange_fn, user: UnsafeMutableRawPointer) throws(PairingError)
        -> halyard_regist_record {
        guard seed.count == 16 else { throw .associationFailed("the registration seed is not 16 bytes") }
        var params = halyard_account_regist_params()
        params.is_ps5 = family == .ps5 ? 1 : 0
        copy(Pairing.normalisedAccountID(accountID), into: &params.account_id)
        copy(clientIP, into: &params.client_ip)
        withUnsafeMutableBytes(of: &params.seed) { $0.copyBytes(from: seed) }

        var result = halyard_account_regist_result()
        let ok = serial.withLock { _ in halyard_account_regist_run(&params, exchange, user, &result) }
        defer {
            withUnsafeMutableBytes(of: &params) { _ = $0.initializeMemory(as: UInt8.self, repeating: 0) }
            withUnsafeMutableBytes(of: &result) { _ = $0.initializeMemory(as: UInt8.self, repeating: 0) }
        }
        guard ok == 1, result.status == HALYARD_ACCOUNT_REGIST_OK else {
            throw .accountRegistrationFailed(status: String(cString: halyard_account_regist_status_text(result.status)),
                                             httpStatus: Int(result.http_status),
                                             consoleReason: cString(result.console_reason))
        }
        return result.record
    }
}

// MARK: - Where the 9303 association is aimed

/// An IPv4 endpoint as the C side takes one: address bytes in network order, port in host order.
struct ConsolePath: Sendable, Equatable {
    var address: [UInt8]
    var port: UInt16
    var text: String

    /// halyard_wan_parse_ipv4: the strict dotted quad a console sends, and nothing else.
    static func ipv4(_ text: String) -> [UInt8]? {
        var out = [UInt8](repeating: 0, count: 4)
        return halyard_wan_parse_ipv4(text, &out) == 1 ? out : nil
    }

    init?(address text: String, port: Int) {
        guard let bytes = Self.ipv4(text), (1...65_535).contains(port) else { return nil }
        address = bytes
        self.port = UInt16(port)
        self.text = text
    }

    /// The control association's endpoint: the candidate our ACCEPT names (the transport and the ACCEPT
    /// must agree, RendezvousTransport.swift), else the host we already had, on 9303. .NET's
    /// HalyardAccountConsoleSession.ConsoleEndpoint and halyard_wan_choose_candidate's documented fallback.
    static func control(_ context: AccountTransportContext) -> ConsolePath? {
        if let candidate = context.selectedCandidate, let path = ConsolePath(address: candidate.address, port: candidate.port) {
            return path
        }
        return ConsolePath(address: context.consoleHost, port: Int(HALYARD_WAN_CONTROL_PORT))
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
    let length = Int(HALYARD_CLIENT_HASHED_ID_LENGTH)
    guard context.localHashedID.count == length else { throw .associationFailed("our localHashedId is not \(length) bytes") }
    guard context.consoleHashedID.count == length else {
        throw .associationFailed("the console's OFFER carried no \(length)-byte localHashedId")
    }
}

// MARK: - Account pairing: a leg of its own

/// Account ("no PIN") pairing's transport: one UDP socket and its 9303 association, owned here because no
/// stream follows. The socket is bound at init, so its port is known before the OFFER that advertises it.
public final class DatagramAccountTransport: AccountRouteTransport {
    public typealias Registration = PairedConsole

    public struct Options: Sendable {
        /// The address to bind (dotted quad). Nil binds INADDR_ANY; a test binds 127.0.0.1.
        public var bindAddress: String?
        /// 0 lets the system choose.
        public var localPort: UInt16
        /// Each 9303 stage's deadline, and the quiet window before a re-send. Zero takes the core's defaults
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
    private let box: LegBox

    /// Binds the socket. Throws when it cannot be bound.
    public init(family: ConsoleFamily, accountID: String, consoleName: String = "", consoleID: String = "",
                options: Options = Options()) throws(PairingError) {
        self.family = family
        self.accountID = accountID
        self.consoleName = consoleName
        self.consoleID = consoleID
        self.options = options

        var bind: [UInt8]?
        if let address = options.bindAddress {
            guard let parsed = ConsolePath.ipv4(address) else { throw .associationFailed("\(address) is not an IPv4 address to bind") }
            bind = parsed
        }
        let box = LegBox(log: options.log)
        let opened = bind.map { halyard_rendezvous_leg_open(box.leg, $0, options.localPort, 0) }
            ?? halyard_rendezvous_leg_open(box.leg, nil, options.localPort, 0)
        guard opened == 1 else { throw .associationFailed("could not bind a UDP socket for the control association") }
        self.box = box
        self.localPort = Int(box.leg.pointee.local_port)
    }

    public func makeSeedKeyMaterial() throws -> SeedKeyMaterial { try AccountSeed.makeKeyMaterial() }

    public func recoverSeed(customData1: String, keyMaterial: SeedKeyMaterial, family: ConsoleFamily) -> [UInt8]? {
        AccountSeed.recover(customData1: customData1, keyMaterial: keyMaterial, family: family)
    }

    /// Our Init: aim the leg at the console and put the Init on the wire, without waiting for an answer
    /// (halyard_dgram_channel_begin; .NET's PrepareAsync). The establishment happens in `register`.
    public func openAssociation(_ context: AccountTransportContext) async throws {
        let options = self.options
        try await onQueue { box throws(PairingError) in
            try validateHashedIDs(context)
            guard let path = ConsolePath.control(context) else {
                throw .associationFailed("the console offered no IPv4 candidate, and there is no host to fall back to")
            }
            var dgram = box.dgramOptions(options)
            let attached = halyard_rendezvous_leg_attach(box.leg, path.address, path.port, context.localHashedID,
                                                         context.consoleHashedID, &dgram)
            guard attached == HALYARD_DGRAM_CHANNEL_OK else { throw .associationFailed("aiming the leg (status \(attached.rawValue))") }
            let begun = halyard_dgram_channel_begin(box.channel)
            guard begun == HALYARD_DGRAM_CHANNEL_OK else { throw .associationFailed("our Init (status \(begun.rawValue))") }
            box.path = path
            options.log?("control association begun toward \(path.text):\(path.port) from local port \(box.leg.pointee.local_port)")
        }
    }

    /// /sess/rgst over the association: establishes it, opens a connection, and exchanges.
    public func register(context: AccountTransportContext, seed: [UInt8]) async throws -> PairedConsole {
        let family = self.family, accountID = self.accountID, name = consoleName, consoleID = self.consoleID
        return try await onQueue { box throws(PairingError) in
            guard let path = box.path else { throw .associationFailed("the association was never opened") }
            let clientIP = try Pairing.localAddress(toward: path.text)
            let record = try AccountRegistration.run(family: family, accountID: accountID, clientIP: clientIP, seed: seed,
                                                     exchange: halyard_dgram_regist_exchange,
                                                     user: UnsafeMutableRawPointer(box.channel))
            return PairedConsole(host: ConsolePath.recordHost(context, path: path), name: name, consoleID: consoleID,
                                 accountID: Pairing.normalisedAccountID(accountID), registration: record)
        }
    }

    private func onQueue<T: Sendable>(_ work: @escaping @Sendable (LegBox) throws(PairingError) -> T) async throws -> T {
        let box = self.box
        return try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do { continuation.resume(returning: try work(box)) } catch { continuation.resume(throwing: error) }
            }
        }
    }
}

/// The leg's storage: ~23 KB of C (halyard_dgram_session.h says it belongs on the heap), touched only on the
/// transport's serial queue, or in deinit when nothing else can reach it.
final class LegBox: @unchecked Sendable {
    let leg: UnsafeMutablePointer<halyard_rendezvous_leg>
    var path: ConsolePath?
    private let sink: LogSink?

    init(log: (@Sendable (String) -> Void)?) {
        let raw = calloc(1, MemoryLayout<halyard_rendezvous_leg>.size)!
        leg = raw.bindMemory(to: halyard_rendezvous_leg.self, capacity: 1)
        halyard_rendezvous_leg_init(leg)
        sink = log.map(LogSink.init)
    }

    deinit {
        halyard_rendezvous_leg_close(leg)
        memset(leg, 0, MemoryLayout<halyard_rendezvous_leg>.size)
        free(leg)
    }

    /// The association inside the leg, by its offset: the address halyard_dgram_regist_exchange wants.
    var channel: UnsafeMutablePointer<halyard_dgram_channel> {
        let offset = MemoryLayout<halyard_rendezvous_leg>.offset(of: \halyard_rendezvous_leg.channel)!
        return (UnsafeMutableRawPointer(leg) + offset).assumingMemoryBound(to: halyard_dgram_channel.self)
    }

    func dgramOptions(_ options: DatagramAccountTransport.Options) -> halyard_dgram_options {
        var o = halyard_dgram_options()
        halyard_dgram_options_default(&o)
        if options.stageTimeout > .zero { o.stage_timeout_ms = options.stageTimeout.clampedMilliseconds }
        if options.receiveTimeout > .zero { o.receive_timeout_ms = options.receiveTimeout.clampedMilliseconds }
        if let sink {
            o.log = { ctx, line in
                guard let ctx, let line else { return }
                Unmanaged<LogSink>.fromOpaque(ctx).takeUnretainedValue().log("9303: " + String(cString: line))
            }
            o.log_ctx = Unmanaged.passUnretained(sink).toOpaque()
        }
        return o
    }
}

final class LogSink: Sendable {
    let log: @Sendable (String) -> Void
    init(_ log: @escaping @Sendable (String) -> Void) { self.log = log }
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

    /// halyard_client_rendezvous_begin, on the session thread: step 5 of halyard_client.h's route.
    public func openAssociation(_ context: AccountTransportContext) async throws {
        try await link.begin(context)
    }

    /// Step 5a: halyard_account_regist_run over halyard_client_rendezvous_exchange, on the session thread.
    /// The record is returned for the caller to log or keep; .NET's connect does not store it.
    public func register(context: AccountTransportContext, seed: [UInt8]) async throws -> PairedConsole {
        try await link.register(context: context, seed: seed, family: family, accountID: accountID)
    }
}

// MARK: - The WAN rendezvous, over the session's prepared leg

/// WanRendezvous's transport over the control leg halyard_client_rendezvous_prepare bound and asked STUN
/// about: the port it advertises and the mapping it offers are that socket's, as a NAT binding is per port.
struct LegWanTransport: WanRendezvousTransport {
    let leg: RendezvousLeg
    var localPort: Int { leg.localPort }
    func gatherReflexive() async -> UDPEndpoint? { leg.reflexive }
}

extension Duration {
    /// Whole milliseconds, clamped to what a C `unsigned` deadline holds.
    var clampedMilliseconds: UInt32 {
        let (seconds, attoseconds) = components
        let ms = seconds.multipliedReportingOverflow(by: 1000)
        guard !ms.overflow else { return seconds > 0 ? UInt32.max : 0 }
        return UInt32(clamping: max(0, ms.partialValue + attoseconds / 1_000_000_000_000_000))
    }
}
