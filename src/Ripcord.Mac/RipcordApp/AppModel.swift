// The app's state that outlives a window: the paired consoles, where they are kept, and the settings a
// stream starts from. Main-actor only; sessions report back to it on the main actor.

import Foundation
import Observation
import RipcordKit

/// Which console a stream window is for. Codable and Hashable so SwiftUI can open and restore windows by it.
struct StreamTarget: Codable, Hashable, Identifiable {
    var consoleKey: String
    var id: String { consoleKey }
}

/// What a stream asks the console for, kept across launches.
struct StreamSettings: Codable, Equatable {
    var width = 1920
    var height = 1080
    var fps = 60
    /// 25 Mb/s: the console grants resolution by bitrate (journal, 2026-09-25).
    var bitrateKbps = 25_000
    var allowHEVC = true
    var hdr = false
    var reportConnectionQuality = false
    var restConsoleOnDisconnect = false

    static let key = "streamSettings"

    static func load() -> StreamSettings {
        guard let data = UserDefaults.standard.data(forKey: key),
              let s = try? JSONDecoder().decode(StreamSettings.self, from: data) else { return StreamSettings() }
        return s
    }

    func save() {
        if let data = try? JSONEncoder().encode(self) { UserDefaults.standard.set(data, forKey: Self.key) }
    }
}

@MainActor
@Observable
final class AppModel {
    /// The app keeps pairings in the Keychain (docs/macos-plan.md, "Credentials").
    let store: any PairingStore
    private(set) var consoles: [PairedConsole] = []
    var settings = StreamSettings.load() {
        didSet { settings.save() }
    }

    init(store: any PairingStore = KeychainPairingStore()) {
        self.store = store
        reload()
    }

    func reload() {
        consoles = store.load().sorted { ($0.name, $0.host) < ($1.name, $1.host) }
    }

    func console(for target: StreamTarget) -> PairedConsole? {
        consoles.first { key(of: $0) == target.consoleKey }
    }

    func key(of console: PairedConsole) -> String {
        console.consoleID.isEmpty ? console.host : console.consoleID
    }

    /// Brings in what `ripcord-lab` paired, so a console paired from the command line is not paired twice.
    @discardableResult
    func importLabPairings() -> Int {
        var imported = 0
        for console in PairingFileStore.lab.load() where !consoles.contains(where: { key(of: $0) == key(of: console) }) {
            if (try? store.save(console)) != nil { imported += 1 }
        }
        reload()
        return imported
    }

    func save(_ console: PairedConsole) throws {
        try store.save(console)
        reload()
    }

    func forget(_ console: PairedConsole) throws {
        try store.remove(console)
        reload()
    }
}
