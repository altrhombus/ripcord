// The app's state that outlives a window: the paired consoles, where they are kept, what the network says
// about them, the account, and the settings a stream starts from. Main-actor only; sessions and scans
// report back to it on the main actor.

import AppKit
import Foundation
import SwiftUI
import WidgetKit
import GameController
import Observation
import RipcordKit

/// Which console a stream window is for. Codable and Hashable so SwiftUI can open and restore windows by it.
struct StreamTarget: Codable, Hashable, Identifiable {
    var consoleKey: String
    var id: String { consoleKey }
}

@MainActor
@Observable
final class AppModel {
    /// The one model. App Intents reach the running app through it, and SwiftUI's scenes hold it.
    static let shared = AppModel()

    /// The app keeps pairings in the Keychain (docs/macos-plan.md, "Credentials").
    let store: any PairingStore
    let account: AccountModel
    let network = NetworkWatch()
    let input: InputRouter
    private(set) var consoles: [PairedConsole] = []
    var settings = StreamSettings.load() {
        didSet {
            settings.save()
            if settings.keyBindings != oldValue.keyBindings { input.hub.bindings = settings.inputBindings }
        }
    }

    /// The library's selection, by console key.
    var selection: String?
    /// Set to show the pairing sheet; `preselected` when it was opened from a console found on the network.
    var pairing: PairingRequest?
    /// Where on screen each console's tile was when it was opened, so its stream window can grow out of it
    /// (DESIGN.md, "The launch"). Taken once by the window.
    @ObservationIgnored var launchOrigins: [String: NSRect] = [:]
    /// Stream windows open now, by console key, so the library and the menu bar can say which are live.
    private(set) var liveStreams: [String: SessionStage] = [:]
    /// Opens a scene, handed over by the first view to appear, for callers with no view of their own (App
    /// Intents, the menu bar).
    @ObservationIgnored var openWindow: OpenWindowAction?
    /// Closes each open stream window, registered by the window, for Disconnect.
    @ObservationIgnored var closers: [String: () -> Void] = [:]
    @ObservationIgnored private var lastSnapshot: ConsoleSnapshot?

    init(store: any PairingStore = KeychainPairingStore()) {
        self.store = store
        account = AccountModel()
        input = InputRouter(bindings: StreamSettings.load().inputBindings)
        reload()
        network.onAddressChange = { [weak self] console, address in self?.moved(console, to: address) }
        network.onRound = { [weak self] in self?.shareSnapshot() }
        network.watch { [weak self] in self?.consoles ?? [] }
        Task { await account.restore() }
        shareSnapshot()
    }

    func reload() {
        defer { shareSnapshot() }
        consoles = store.load().sorted {
            let order = $0.name.localizedStandardCompare($1.name)
            return order == .orderedSame ? $0.host < $1.host : order == .orderedAscending
        }
    }

    func console(for target: StreamTarget) -> PairedConsole? {
        consoles.first { key(of: $0) == target.consoleKey }
    }

    func console(forKey key: String) -> PairedConsole? {
        consoles.first { self.key(of: $0) == key }
    }

    func key(of console: PairedConsole) -> String {
        console.consoleID.isEmpty ? console.host : console.consoleID
    }

    func reachability(of console: PairedConsole) -> Reachability {
        network.reachability(of: console)
    }

    /// Consoles the network reports that are not paired, for the library's Pair tiles.
    var unpairedNearby: [DiscoveredConsole] {
        network.found.values.filter { found in
            found.family != nil && !consoles.contains { $0.consoleID == found.hostID || $0.host == found.address }
        }
        .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
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

    func rename(_ console: PairedConsole, to name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed != console.name else { return }
        var renamed = console
        renamed.name = trimmed
        try? save(renamed)
    }

    func forget(_ console: PairedConsole) throws {
        try store.remove(console)
        reload()
    }

    /// Sends a WAKEUP without connecting. Nothing answers one, so the tile learns the outcome from the scan.
    func wake(_ console: PairedConsole) {
        Task.detached { _ = try? LANWake.send(to: console) }
    }

    /// A paired console answered from a new address (DHCP gave it another lease): the record follows it,
    /// matched by the console's own id, so the tile does not go to Not found for a console that is right there.
    private func moved(_ console: PairedConsole, to address: String) {
        var updated = console
        updated.host = address
        try? save(updated)
    }

    func streamStarted(_ key: String) { liveStreams[key] = .idle; shareSnapshot() }
    func streamStage(_ key: String, _ stage: SessionStage) { if liveStreams[key] != nil { liveStreams[key] = stage } }
    func streamEnded(_ key: String) { liveStreams[key] = nil; closers[key] = nil; shareSnapshot() }

    // MARK: Across the Mac (DESIGN.md, "Present across the Mac")

    var consoleEntities: [ConsoleEntity] {
        consoles.map { ConsoleEntity(id: key(of: $0), name: $0.name.isEmpty ? $0.host : $0.name, family: $0.family.rawValue) }
    }

    /// Opens the stream window for a console, from anywhere: an intent, the menu bar, a widget.
    func openStream(_ key: String) {
        guard console(forKey: key) != nil else { return }
        NSApp.activate()
        openWindow?(id: "stream", value: StreamTarget(consoleKey: key))
    }

    func wake(key: String) {
        if let console = console(forKey: key) { wake(console) }
    }

    func disconnect(key: String) {
        closers[key]?()
    }

    /// Writes what the widgets show, when it has changed, and asks WidgetKit to redraw. The states come from
    /// the network watch, so this runs after each scan as well as when the consoles change.
    func shareSnapshot() {
        let snapshot = ConsoleSnapshot(consoles: consoles.map { console in
            let key = key(of: console)
            let state: String
            if liveStreams[key] != nil {
                state = "playing"
            } else {
                state = switch reachability(of: console) {
                case .ready: "ready"
                case .resting: "resting"
                case .notFound: "notFound"
                case .unknown: "unknown"
                }
            }
            return ConsoleSnapshot.Console(id: key, name: console.name.isEmpty ? console.host : console.name,
                                           family: console.family.rawValue, state: state)
        }, written: .now)
        guard snapshot.consoles != lastSnapshot?.consoles else { return }
        lastSnapshot = snapshot
        snapshot.save()
        WidgetCenter.shared.reloadAllTimelines()
    }
}

struct PairingRequest: Identifiable {
    let id = UUID()
    var preselected: DiscoveredConsole?
}
