// What every call into the Rust engine shares: the platform's CSPRNG as the engine takes it, and the
// conversions between the engine's registration records and PairedConsole.

internal import CRipcordEngine
import Foundation
import Security

enum EngineRandom {
    /// SecRandomCopyBytes behind RipcordRandom. A refusal fails what asked, never supplies zeroes.
    static var table: RipcordRandom {
        RipcordRandom(user: nil, fill: { _, out, length in
            guard let out else { return false }
            return SecRandomCopyBytes(kSecRandomDefault, length, out) == errSecSuccess
        })
    }
}

enum EngineRecords {
    static func console(_ r: RipcordPairingRecord, host: String, name: String, consoleID: String,
                        accountID: String) -> PairedConsole {
        let key = withUnsafeBytes(of: r.registration_key) { Array($0.prefix(r.registration_key_length)) }
        let companion = withUnsafeBytes(of: r.companion) { Array($0) }
        return PairedConsole(host: host, name: name, consoleID: consoleID, accountID: accountID,
                             family: r.is_ps5 ? .ps5 : .ps4, registrationKey: key, companion: companion)
    }

    static func reason(_ r: RipcordRegistResult) -> String {
        withUnsafeBytes(of: r.console_reason) { String(decoding: $0.prefix(while: { $0 != 0 }), as: UTF8.self) }
    }

    /// The registration key and companion do not outlive the call that received them.
    static func wipe(_ r: inout RipcordRegistResult) {
        withUnsafeMutableBytes(of: &r.record) { _ = $0.initializeMemory(as: UInt8.self, repeating: 0) }
    }

    static func statusText(_ status: UInt32) -> String {
        switch Int32(status) {
        case RIPCORD_REGIST_OK: "ok"
        case RIPCORD_REGIST_BAD_PARAMS: "a missing account id or client address"
        case RIPCORD_REGIST_NO_RANDOM: "this Mac could not produce random bytes"
        case RIPCORD_REGIST_CONNECT: "TCP 9295 refused or unreachable"
        case RIPCORD_REGIST_SEND: "the request could not be sent"
        case RIPCORD_REGIST_NO_REPLY: "the console connected, then said nothing"
        case RIPCORD_REGIST_MALFORMED: "the console's answer was not one this understands"
        case RIPCORD_REGIST_REFUSED: "the console refused"
        case RIPCORD_REGIST_BAD_RECORD: "the answer did not decrypt: a wrong PIN, or a wrong seed"
        case RIPCORD_REGIST_TRANSPORT: "the 9303 exchange failed"
        default: "status \(status)"
        }
    }
}
