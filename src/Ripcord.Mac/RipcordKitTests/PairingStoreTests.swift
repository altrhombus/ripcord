// Where pairings live: the lab's owner-only file, its one-time import of the C core's pairing.txt, and the
// Keychain the app keeps them in. Every key here is synthetic.

import Foundation
@testable import RipcordKit
import Testing

@Suite("Pairing stores", .serialized)
struct PairingStoreTests {
    static func console(_ id: String = "cid-1", host: String = "192.0.2.7") -> PairedConsole {
        PairedConsole(host: host, name: "Living room", consoleID: id, accountID: "1234567890123456789", family: .ps5,
                      registrationKey: Array("1a2b3c4d".utf8), companion: (0..<16).map { UInt8($0) },
                      deviceID: [9, 9], loginPIN: "2468")
    }

    static func directory() -> URL {
        FileManager.default.temporaryDirectory.appending(path: "ripcord-pairing-\(UUID().uuidString)")
    }

    @Test("the file store round-trips, replaces by console id, and is owner-only")
    func fileStore() throws {
        let dir = Self.directory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = PairingFileStore(directory: dir)
        #expect(store.load().isEmpty)
        try store.save(Self.console())
        var moved = Self.console()
        moved.host = "192.0.2.8"
        try store.save(moved)                                  // same console, new address: replaced
        try store.save(Self.console("cid-2", host: "192.0.2.9"))
        let loaded = store.load()
        #expect(loaded.count == 2)
        #expect(loaded.first { $0.consoleID == "cid-1" } == moved)
        let mode = try FileManager.default.attributesOfItem(atPath: store.file.path)[.posixPermissions] as? Int
        #expect(mode == 0o600)
        try store.remove(moved)
        #expect(store.load().map(\.consoleID) == ["cid-2"])
    }

    @Test("the C core's pairing.txt is imported once, sections and all, then renamed")
    func legacyImport() throws {
        let dir = Self.directory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let legacy = """
        # Written by ripcord after pairing.
        selected=0
        osmajor=10
        accountid=1234567890123456789

        [console]
        host=192.0.2.7
        name=Living room
        consoleid=cid-1
        platform=ps5
        registkey=3161326233633464
        companion=000102030405060708090a0b0c0d0e0f
        deviceid=0909

        [console]
        host=192.0.2.9
        platform=ps4
        registkey=aabb
        companion=ffffffffffffffffffffffffffffffff
        """
        try legacy.write(to: dir.appending(path: "pairing.txt"), atomically: true, encoding: .utf8)

        let store = PairingFileStore(directory: dir)
        let loaded = store.load()
        #expect(loaded.count == 2)
        let first = try #require(loaded.first)
        #expect(first.host == "192.0.2.7" && first.name == "Living room" && first.consoleID == "cid-1")
        #expect(first.registrationKey == Array("1a2b3c4d".utf8) && first.companion == (0..<16).map { UInt8($0) })
        #expect(first.accountID == "1234567890123456789", "the shared account id reaches every console")
        #expect(first.deviceID == [9, 9] && first.family == .ps5)
        #expect(loaded[1].family == .ps4 && loaded[1].accountID == "1234567890123456789")
        #expect(FileManager.default.fileExists(atPath: dir.appending(path: "pairing.txt.imported").path))
        #expect(!FileManager.default.fileExists(atPath: dir.appending(path: "pairing.txt").path))
        #expect(store.load() == loaded, "read back from pairings.json, not imported again")

        // A file from before sections: one console in the shared block.
        let old = LegacyPairingFile.parse("host=192.0.2.1\nregistkey=aabbccdd\ncompanion=\(String(repeating: "11", count: 16))\npin=1234\n")
        #expect(old.count == 1 && old.first?.loginPIN == "1234" && old.first?.registrationKey == [0xaa, 0xbb, 0xcc, 0xdd])
        #expect(LegacyPairingFile.parse("host=192.0.2.1\nregistkey=aa\ncompanion=11\n").isEmpty, "a short companion is not a record")
    }

    @Test("the Keychain store round-trips and removes, in a service of its own",
          .enabled(if: KeychainPairingStore.probe(), "no usable login keychain here"))
    func keychainStore() throws {
        let store = KeychainPairingStore(service: "io.github.altrhombus.Ripcord.pairing.tests.\(UUID().uuidString)")
        defer {
            for c in store.load() { try? store.remove(c) }
        }
        try store.save(Self.console())
        try store.save(Self.console("cid-2"))
        var moved = Self.console()
        moved.host = "192.0.2.8"
        try store.save(moved)
        let loaded = store.load().sorted { $0.consoleID < $1.consoleID }
        #expect(loaded.map(\.consoleID) == ["cid-1", "cid-2"])
        #expect(loaded.first == moved)
        try store.remove(moved)
        #expect(store.load().map(\.consoleID) == ["cid-2"])
    }
}

extension KeychainPairingStore {
    /// Whether this machine has a keychain the test can write: a CI runner may not.
    static func probe() -> Bool {
        let store = KeychainPairingStore(service: "io.github.altrhombus.Ripcord.pairing.probe")
        let probe = PairedConsole(host: "192.0.2.1", name: "", consoleID: "probe", accountID: "", family: .ps5,
                                  registrationKey: [1], companion: Array(repeating: 0, count: 16))
        guard (try? store.save(probe)) != nil else { return false }
        try? store.remove(probe)
        return true
    }
}
