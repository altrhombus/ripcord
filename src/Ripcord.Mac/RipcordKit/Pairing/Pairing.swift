// PIN pairing: what only the host knows (this Mac's own address, the person's account id and PIN) handed
// to the engine's registration (ripcord_regist_pin: the arm probe, the encrypted request, the decrypted
// reply). The record comes back as a PairedConsole, and where it is kept is PairingStore.swift's.

internal import CRipcordEngine
import Darwin
import Foundation

public enum PairingError: Error, Sendable, CustomStringConvertible {
    case invalidAccountID(String)
    case invalidPIN
    case noRouteToConsole(errno: Int32)
    /// The console, or the way to it, refused. `sawSearchReply` separates "never reached it" from "it
    /// answered and said no", which is the first question anyone debugging this asks.
    case failed(status: String, httpStatus: Int, consoleReason: String, sawSearchReply: Bool)
    case couldNotSave(path: String)
    /// The account route's /sess/rgst, over the 9303 association.
    case accountRegistrationFailed(status: String, httpStatus: Int, consoleReason: String)
    /// The association could not be opened or aimed.
    case associationFailed(String)

    public var description: String {
        switch self {
        case .invalidAccountID(let why): "the account id is not usable: \(why)"
        case .invalidPIN: "the PIN must be the 8 digits the console shows"
        case .noRouteToConsole(let e): "no route to the console (\(String(cString: strerror(e))))"
        case let .failed(status, http, reason, saw):
            "pairing failed: \(status); HTTP \(http)\(reason.isEmpty ? "" : ", console reason \(reason)"); the search probe was \(saw ? "answered" : "NOT answered")"
        case .couldNotSave(let path): "paired, but the record could not be saved to \(path)"
        case let .accountRegistrationFailed(status, http, reason):
            "account pairing failed: \(status); HTTP \(http)\(reason.isEmpty ? "" : ", console reason \(reason)")"
        case .associationFailed(let why): "the 9303 control association failed: \(why)"
        }
    }
}

public enum Pairing {
    /// Registers this Mac with a console showing its pairing PIN. Blocking: about two seconds arming the
    /// console's listener, then the exchange.
    public static func register(host: String, family: ConsoleFamily, accountID: String, pin: String,
                                name: String = "", consoleID: String = "") throws(PairingError) -> PairedConsole {
        let digits = pin.filter { !$0.isWhitespace }
        guard digits.count == 8, digits.allSatisfy(\.isNumber), let passcode = UInt32(digits) else { throw .invalidPIN }
        let account = try normalise(accountID)
        guard let address = ConsolePath.ipv4(host) else { throw .noRouteToConsole(errno: EINVAL) }
        let clientIP = try localAddress(toward: host)

        var random = EngineRandom.table
        var result = RipcordRegistResult()
        let status = address.withUnsafeBufferPointer { a in
            ripcord_regist_pin(a.baseAddress, family == .ps5, account, passcode, clientIP, 0, false, &random, &result)
        }
        defer { EngineRecords.wipe(&result) }
        guard status == RIPCORD_STATUS_OK, result.status == UInt32(RIPCORD_REGIST_OK) else {
            throw .failed(status: EngineRecords.statusText(result.status), httpStatus: Int(result.http_status),
                          consoleReason: EngineRecords.reason(result), sawSearchReply: result.saw_arm_reply)
        }
        return EngineRecords.console(result.record, host: host, name: name, consoleID: consoleID, accountID: account)
    }

    /// A typed account id as decimal, or why it is not one, in words a person can act on.
    static func normalise(_ accountID: String) throws(PairingError) -> String {
        var out = [UInt8](repeating: 0, count: 32)
        var reason = [UInt8](repeating: 0, count: 128)
        let code = ripcord_account_id_normalise(accountID, &out, out.count, &reason, reason.count)
        guard code == UInt32(RIPCORD_ACCOUNT_ID_OK) else {
            throw .invalidAccountID(String(decoding: reason.prefix(while: { $0 != 0 }), as: UTF8.self))
        }
        return String(decoding: out.prefix(while: { $0 != 0 }), as: UTF8.self)
    }

    /// An account id as the registration wants it: normalised to decimal when it reads as a 64-bit id, as
    /// the PIN route insists; otherwise as given, which is what the .NET account route sends (the cloud tier
    /// supplies it, not a person, so there is no typing mistake to catch).
    static func normalisedAccountID(_ accountID: String) -> String {
        (try? normalise(accountID)) ?? accountID
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

