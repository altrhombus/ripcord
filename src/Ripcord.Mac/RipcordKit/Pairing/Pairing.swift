// PIN pairing, and where the result is kept.
//
// The exchange itself is libripcord's halyard_regist_run (session/halyard_regist_flow.h): the search
// probe the console insists precede a registration, the encrypted request, and the decrypted reply. This
// file supplies what only the host knows (this Mac's own address, the person's account id and PIN) and
// turns the result into a stored record exactly as the PS3 port does (rc_pair_ps3.c), so the record
// format stays one format.
//
// STORAGE IS THE LAB'S FOR NOW. The record holds the console's registration key, which is a secret, so the
// file is written readable by its owner only. The app keeps records in the Keychain instead
// (docs/macos-plan.md); until then this is the core's own pairing file, under Application Support.

internal import CLibripcord
import Darwin
import Foundation

public struct PairedConsole: Sendable {
    public var host: String { cString(record.host) }
    public var name: String { cString(record.name) }
    public var consoleID: String { cString(record.console_id) }
    public var family: ConsoleFamily { record.is_ps5 != 0 ? .ps5 : .ps4 }

    var record: halyard_pairing_record
}

public enum PairingError: Error, Sendable, CustomStringConvertible {
    case invalidAccountID(String)
    case invalidPIN
    case noRouteToConsole(errno: Int32)
    /// The console, or the way to it, refused. `sawSearchReply` separates "never reached it" from "it
    /// answered and said no", which is the first question anyone debugging this asks.
    case failed(status: String, httpStatus: Int, consoleReason: String, sawSearchReply: Bool)
    case couldNotSave(path: String)

    public var description: String {
        switch self {
        case .invalidAccountID(let why): "the account id is not usable: \(why)"
        case .invalidPIN: "the PIN must be the 8 digits the console shows"
        case .noRouteToConsole(let e): "no route to the console (\(String(cString: strerror(e))))"
        case let .failed(status, http, reason, saw):
            "pairing failed: \(status); HTTP \(http)\(reason.isEmpty ? "" : ", console reason \(reason)"); the search probe was \(saw ? "answered" : "NOT answered")"
        case .couldNotSave(let path): "paired, but the record could not be saved to \(path)"
        }
    }
}

public enum Pairing {
    /// Registers this Mac with a console showing its pairing PIN.
    public static func register(host: String, family: ConsoleFamily, accountID: String, pin: String,
                                name: String = "", consoleID: String = "") throws(PairingError) -> PairedConsole {
        let digits = pin.filter { !$0.isWhitespace }
        guard digits.count == 8, digits.allSatisfy(\.isNumber), let passcode = UInt32(digits) else { throw .invalidPIN }

        var normalised = [CChar](repeating: 0, count: 64)
        let accountStatus = halyard_account_id_normalise(accountID, &normalised, normalised.count)
        guard accountStatus == HALYARD_ACCOUNT_ID_OK else {
            throw .invalidAccountID(String(cString: halyard_account_id_status_text(accountStatus)))
        }

        var params = halyard_regist_params()
        copy(host, into: &params.host)
        params.is_ps5 = family == .ps5 ? 1 : 0
        withUnsafeMutableBytes(of: &params.account_id) { dst in
            normalised.withUnsafeBytes { src in dst.copyMemory(from: UnsafeRawBufferPointer(rebasing: src.prefix(dst.count - 1))) }
        }
        params.passcode = passcode
        copy(try localAddress(toward: host), into: &params.client_ip)

        var result = halyard_regist_result()
        guard halyard_regist_run(&params, &result) == 1, result.status == HALYARD_REGIST_OK else {
            throw .failed(status: String(cString: halyard_regist_status_text(result.status)),
                          httpStatus: Int(result.http_status), consoleReason: cString(result.console_reason),
                          sawSearchReply: result.saw_search_reply != 0)
        }

        // rc_pair_ps3.c, "The record is the product of all of this, and the only place it exists."
        var record = halyard_pairing_record()
        copy(host, into: &record.host)
        copy(name, into: &record.name)
        copy(consoleID, into: &record.console_id)
        withUnsafeMutableBytes(of: &record.account_id) { dst in
            withUnsafeBytes(of: params.account_id) { dst.copyMemory(from: UnsafeRawBufferPointer(rebasing: $0.prefix(dst.count))) }
        }
        record.is_ps5 = result.record.is_ps5
        withUnsafeMutableBytes(of: &record.registkey) { dst in
            withUnsafeBytes(of: result.record.registration_key) {
                dst.copyMemory(from: UnsafeRawBufferPointer(rebasing: $0.prefix(result.record.registration_key_length)))
            }
        }
        record.registkey_length = result.record.registration_key_length
        record.companion = result.record.companion
        return PairedConsole(record: record)
    }

    /// This Mac's address on the interface that routes to `host`: what the console expects in the
    /// registration's HOST header. A connected UDP socket asks the kernel's routing table and sends nothing.
    static func localAddress(toward host: String) throws(PairingError) -> String {
        let sock = socket(AF_INET, SOCK_DGRAM, 0)
        guard sock >= 0 else { throw .noRouteToConsole(errno: errno) }
        defer { close(sock) }
        guard var peer = sockaddr_in.ipv4(host, port: 9295) else { throw .noRouteToConsole(errno: EINVAL) }
        let connected = withUnsafePointer(to: &peer) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(sock, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard connected == 0 else { throw .noRouteToConsole(errno: errno) }
        var local = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let named = withUnsafeMutablePointer(to: &local) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(sock, $0, &length) }
        }
        guard named == 0 else { throw .noRouteToConsole(errno: errno) }
        var text = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
        inet_ntop(AF_INET, &local.sin_addr, &text, socklen_t(text.count))
        return String(decoding: text.prefix(while: { $0 != 0 }).map { UInt8(bitPattern: $0) }, as: UTF8.self)
    }
}

/// The lab's pairing store: the core's own pairing file, in a directory of the caller's choosing.
public struct PairingStore: Sendable {
    public let directory: URL

    public init(directory: URL) { self.directory = directory }

    /// ~/Library/Application Support/Ripcord/lab
    public static var lab: PairingStore {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return PairingStore(directory: base.appending(path: "Ripcord/lab", directoryHint: .isDirectory))
    }

    /// The core's file functions take a path whose DIRECTORY they use (they were written for argv[0]).
    private var anchor: String { directory.appending(path: "ripcord").path }

    public func save(_ console: PairedConsole) throws(PairingError) {
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                    attributes: [.posixPermissions: 0o700])
        } catch { throw .couldNotSave(path: directory.path) }
        var record = console.record
        let previous = umask(0o077)   // the file carries registration keys: owner-only
        defer { umask(previous) }
        guard halyard_pairing_file_save(anchor, &record) == 1 else { throw .couldNotSave(path: directory.path) }
    }

    public func load() -> [PairedConsole] {
        var set = halyard_pairing_set()
        guard halyard_pairing_file_load_set(anchor, &set) > 0 else { return [] }
        return withUnsafeBytes(of: set.console) { raw in
            let records = raw.bindMemory(to: halyard_pairing_record.self)
            return (0..<Int(set.count)).map { PairedConsole(record: records[$0]) }
        }
    }
}

/// Copies a Swift string into a C `char[N]` field, NUL-terminated and truncated to fit.
func copy<T>(_ string: String, into field: inout T) {
    withUnsafeMutableBytes(of: &field) { dst in
        dst.initializeMemory(as: UInt8.self, repeating: 0)
        let bytes = Array(string.utf8.prefix(dst.count - 1))
        bytes.withUnsafeBytes { dst.copyMemory(from: $0) }
    }
}
