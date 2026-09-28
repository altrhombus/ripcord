// This Mac's identity to the cloud: the 16-byte device id, the `duid` built from it, and the signaling
// `localHashedId` built from that. Ported from HalyardClientDeviceId.cs, HalyardLocalHashedId.cs and
// Ripcord.Core's IDeviceIdentity.
//
// The derivations are .NET's exactly, and tested against its output for a fixed synthetic id: the duid is
// a constant 8-byte prefix plus the device id in lowercase hex, and the hashed id is SHA-1 over that
// string's ASCII bytes.

import CryptoKit
import Foundation
import Security

public enum ClientDeviceID {
    /// The observed 8-byte prefix, as hex. A required on-wire value, generic to every PC client.
    ///
    /// **[X] Its meaning is assumed, not confirmed.** It reads like a device-type tag and a capability word,
    /// but that is inference from one observation. The value is [C] captured; the interpretation is not.
    public static let prefix = "0000000700410080"

    /// 48 hex characters: the 8-byte prefix and the 16-byte device id.
    public static let hexLength = 48

    /// The `duid` for `deviceID`. Throws rather than inventing one: the id must be stable across runs, and a
    /// client presenting a different one each launch would pile up device registrations on the account.
    public static func make(from deviceID: [UInt8]) throws(CloudError) -> String {
        guard deviceID.count == 16 else { throw .deviceIDUnavailable(length: deviceID.count) }
        return prefix + deviceID.lowercaseHex
    }
}

/// The 20-byte id a peer publishes in its signaling OFFER and names itself by in the 9303 control prelude.
///
/// **The vendor's own derivation is [X] unknown.** Its value is stable for one machine across every capture,
/// and SHA-1 or truncated SHA-2/MD5 over the obvious inputs all miss. So this is our own, and says so: SHA-1
/// of the client device id, which is stable for this machine and needs no new state. What the console
/// appears to need is that the OFFER and the prelude agree; that is a hypothesis, and the live pairing run is
/// its test. If a console rejects our prelude while accepting everything before it, suspect this first.
public enum LocalHashedID {
    public static let length = 20

    public static func make(clientDeviceID: String) throws(CloudError) -> [UInt8] {
        guard !clientDeviceID.isEmpty else { throw .invalidArgument("clientDeviceId must not be empty") }
        // SHA-1 for its length, not its security: an identifier the console compares, never a credential.
        // ASCII as .NET's Encoding.ASCII, which writes "?" for anything outside it; a duid is hex, so the
        // difference never arises, but the substitution keeps the two byte-identical if it ever did.
        let ascii = clientDeviceID.unicodeScalars.map { $0.isASCII ? UInt8($0.value) : UInt8(ascii: "?") }
        return Array(Insecure.SHA1.hash(data: ascii))
    }

    public static func make(deviceID: [UInt8]) throws(CloudError) -> [UInt8] {
        try make(clientDeviceID: ClientDeviceID.make(from: deviceID))
    }
}

/// Where this Mac's 16-byte device id comes from.
///
/// **Generated once and kept in the Keychain, rather than read from the hardware.** The .NET reference reads
/// Windows' MachineGuid, which is per installation of the OS, and says of macOS only that "callers should
/// inject one". The Mac's nearest equivalent to hand is the hardware UUID, but that identifies the machine
/// across every reinstall and to every other app, and this value is sent to a third-party service in each
/// sign-in. A random id per user, kept where the user's other secrets are, has the property that matters
/// (stable across launches, so the account does not collect device registrations) without that one.
/// Losing it (a reset Keychain) costs one extra device registration, which is what a reinstall costs on
/// Windows.
public enum DeviceIdentity {
    public static let keychainAccount = "client-device-id"

    /// The stored id, creating and storing it on first use.
    public static func stable(service: String = KeychainAccountTokenStore.defaultService) throws(CloudError) -> [UInt8] {
        if let existing = try Keychain.read(service: service, account: keychainAccount), existing.count == 16 {
            return Array(existing)
        }
        var bytes = [UInt8](repeating: 0, count: 16)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        guard status == errSecSuccess else { throw .keychain(status) }
        try Keychain.write(Data(bytes), service: service, account: keychainAccount,
                           label: "Ripcord device identifier")
        return bytes
    }

    /// A GUID or UUID string (dashes optional) as 16 bytes: `DefaultDeviceIdentity.TryParseMachineGuid`.
    /// For a host that supplies its own id, or an override in a harness.
    public static func parse(_ text: String) -> [UInt8]? {
        let hex = text.replacingOccurrences(of: "-", with: "").trimmingCharacters(in: .whitespaces)
        guard hex.count == 32, hex.allSatisfy(\.isHexDigit) else { return nil }
        var bytes: [UInt8] = []
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            guard let byte = UInt8(hex[index..<next], radix: 16) else { return nil }
            bytes.append(byte)
            index = next
        }
        return bytes
    }
}
