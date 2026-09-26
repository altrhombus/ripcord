// Where pairings are kept: the lab's owner-only file, or the Keychain for the app (docs/macos-plan.md,
// "Credentials"). Storage is the host's, not the engine's: registration hands back a record, and this is
// the one place that decides where it lives. Both stores keep the same record, so the lab and the app
// differ only in the store they are given.
//
// THE LEGACY FILE. Until the Rust engine, the lab kept pairings in the C core's own `pairing.txt`
// (libripcord/session/halyard_pairing_file.c): shared settings at the top, then one `[console]` section per
// console, or a single record with no sections from before sections existed. PairingFileStore imports it
// once, on the first load that finds it with no `pairings.json` beside it, and renames it
// `pairing.txt.imported` so the import does not repeat. The reader is Swift's own, so importing needs no C.

import Foundation
import Security

/// A console this Mac is paired with. Carries the registration key and the companion, which are secrets:
/// never log a PairedConsole, and keep it only in a PairingStore.
public struct PairedConsole: Sendable, Codable, Equatable {
    public var host: String
    public var name: String
    public var consoleID: String
    /// The PSN account the pairing was made for, as the console expects it.
    public var accountID: String
    public var family: ConsoleFamily
    var registrationKey: [UInt8]
    var companion: [UInt8]
    /// RP-Did. Empty means this Mac's own identity, supplied at connect.
    var deviceID: [UInt8]
    /// A stored passcode, used for the first sign-in attempt without asking anyone.
    var loginPIN: String?

    public init(host: String, name: String, consoleID: String, accountID: String, family: ConsoleFamily,
                registrationKey: [UInt8], companion: [UInt8], deviceID: [UInt8] = [], loginPIN: String? = nil) {
        self.host = host
        self.name = name
        self.consoleID = consoleID
        self.accountID = accountID
        self.family = family
        self.registrationKey = registrationKey
        self.companion = companion
        self.deviceID = deviceID
        self.loginPIN = loginPIN
    }

    /// The registration key's presence, for a caller checking a record is usable. Not the key.
    public var hasRegistrationKey: Bool { !registrationKey.isEmpty }

    /// Which entry a store replaces on save: the console's id when it has one, else its address.
    var storeKey: String { consoleID.isEmpty ? host : consoleID }
}

extension ConsoleFamily: Codable {}

public protocol PairingStore: Sendable {
    /// Adds the console, replacing an existing entry for the same console.
    func save(_ console: PairedConsole) throws(PairingError)
    func load() -> [PairedConsole]
    func remove(_ console: PairedConsole) throws(PairingError)
}

// MARK: - The lab's file

/// An owner-only JSON file in a directory of the caller's choosing: `pairings.json`, mode 0600, in a 0700
/// directory, since it carries registration keys.
public struct PairingFileStore: PairingStore {
    public let directory: URL

    public init(directory: URL) { self.directory = directory }

    /// ~/Library/Application Support/Ripcord/lab
    public static var lab: PairingFileStore {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return PairingFileStore(directory: base.appending(path: "Ripcord/lab", directoryHint: .isDirectory))
    }

    var file: URL { directory.appending(path: "pairings.json") }
    var legacyFile: URL { directory.appending(path: "pairing.txt") }

    public func save(_ console: PairedConsole) throws(PairingError) {
        var all = load().filter { $0.storeKey != console.storeKey }
        all.append(console)
        try write(all)
    }

    public func remove(_ console: PairedConsole) throws(PairingError) {
        try write(load().filter { $0.storeKey != console.storeKey })
    }

    public func load() -> [PairedConsole] {
        if let data = try? Data(contentsOf: file) {
            return (try? JSONDecoder().decode([PairedConsole].self, from: data)) ?? []
        }
        return importLegacy()
    }

    private func write(_ consoles: [PairedConsole]) throws(PairingError) {
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                    attributes: [.posixPermissions: 0o700])
            let data = try JSONEncoder().encode(consoles)
            // Created owner-only rather than chmod'ed after, so there is no moment it is readable.
            let temporary = directory.appending(path: ".pairings.json.new")
            FileManager.default.createFile(atPath: temporary.path, contents: nil, attributes: [.posixPermissions: 0o600])
            try data.write(to: temporary)
            _ = try FileManager.default.replaceItemAt(file, withItemAt: temporary)
        } catch {
            throw .couldNotSave(path: directory.path)
        }
    }

    /// The C core's `pairing.txt`, once: read, written as `pairings.json`, renamed so it is not read again.
    private func importLegacy() -> [PairedConsole] {
        guard let text = try? String(contentsOf: legacyFile, encoding: .utf8) else { return [] }
        let consoles = LegacyPairingFile.parse(text)
        guard !consoles.isEmpty, (try? write(consoles)) != nil else { return consoles }
        try? FileManager.default.moveItem(at: legacyFile, to: directory.appending(path: "pairing.txt.imported"))
        return consoles
    }
}

/// The C core's pairing file format, read (halyard_pairing_file.c, halyard_pairing_file_save_set).
enum LegacyPairingFile {
    static func parse(_ text: String) -> [PairedConsole] {
        var shared: [String: String] = [:]
        var sections: [[String: String]] = []
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") { continue }
            if line == "[console]" {
                sections.append([:])
                continue
            }
            guard let eq = line.firstIndex(of: "=") else { continue }
            let key = line[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
            if sections.isEmpty { shared[key] = value } else { sections[sections.count - 1][key] = value }
        }
        // A file from before sections holds one console in the shared block.
        if sections.isEmpty { sections = [shared] }
        return sections.compactMap { entry in
            let get = { (key: String) in entry[key] ?? shared[key] ?? "" }
            guard !get("host").isEmpty, let key = hex(get("registkey")), !key.isEmpty,
                  let companion = hex(get("companion")), companion.count == 16 else { return nil }
            return PairedConsole(host: get("host"), name: get("name"), consoleID: get("consoleid"),
                                 accountID: get("accountid"), family: get("platform") == "ps4" ? .ps4 : .ps5,
                                 registrationKey: key, companion: companion, deviceID: hex(get("deviceid")) ?? [],
                                 loginPIN: get("pin").isEmpty ? nil : get("pin"))
        }
    }

    static func hex(_ text: String) -> [UInt8]? {
        guard text.count.isMultiple(of: 2) else { return nil }
        var out: [UInt8] = []
        var index = text.startIndex
        while index < text.endIndex {
            let next = text.index(index, offsetBy: 2)
            guard let byte = UInt8(text[index..<next], radix: 16) else { return nil }
            out.append(byte)
            index = next
        }
        return out
    }
}

// MARK: - The Keychain, for the app

/// One generic-password item per console, holding the record: readable only while the Mac is unlocked,
/// and never synced or migrated to another device, since a pairing identifies this Mac to the console.
public struct KeychainPairingStore: PairingStore {
    public let service: String

    public init(service: String = "io.github.altrhombus.Ripcord.pairing") { self.service = service }

    private func query(_ account: String? = nil) -> [String: Any] {
        var q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service]
        if let account { q[kSecAttrAccount as String] = account }
        return q
    }

    public func save(_ console: PairedConsole) throws(PairingError) {
        guard let data = try? JSONEncoder().encode(console) else { throw .couldNotSave(path: service) }
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
            kSecAttrLabel as String: "Ripcord pairing: \(console.name.isEmpty ? console.host : console.name)",
        ]
        var status = SecItemUpdate(query(console.storeKey) as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            status = SecItemAdd(query(console.storeKey).merging(attributes) { $1 } as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw .couldNotSave(path: "the Keychain (status \(status))") }
    }

    public func load() -> [PairedConsole] {
        // The accounts first, then each item's data: the Mac's file-based keychain refuses data for more than
        // one item per query.
        var q = query()
        q[kSecMatchLimit as String] = kSecMatchLimitAll
        q[kSecReturnAttributes as String] = true
        var result: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &result) == errSecSuccess else { return [] }
        let items = (result as? [[String: Any]]) ?? (result as? [String: Any]).map { [$0] } ?? []
        return items.compactMap { item in
            guard let account = item[kSecAttrAccount as String] as? String else { return nil }
            var one = query(account)
            one[kSecMatchLimit as String] = kSecMatchLimitOne
            one[kSecReturnData as String] = true
            var data: CFTypeRef?
            guard SecItemCopyMatching(one as CFDictionary, &data) == errSecSuccess, let data = data as? Data else { return nil }
            return try? JSONDecoder().decode(PairedConsole.self, from: data)
        }
    }

    public func remove(_ console: PairedConsole) throws(PairingError) {
        let status = SecItemDelete(query(console.storeKey) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw .couldNotSave(path: "the Keychain (status \(status))")
        }
    }
}
