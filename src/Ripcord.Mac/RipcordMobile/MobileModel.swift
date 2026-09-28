// The mobile app's state that outlives a screen: the paired consoles, where they are kept, what the network
// says about them, and the settings a stream starts from. Main-actor only.

import Foundation
import Observation
import RipcordKit

@MainActor
@Observable
final class MobileModel {
    /// Pairings are kept in the Keychain, as the Mac app keeps them. Whether iCloud Keychain should carry
    /// them between a person's devices is an open decision (docs/ios-plan.md).
    let store: any PairingStore
    let network = NetworkWatch()
    private(set) var consoles: [PairedConsole] = []
    var settings = StreamSettings.load() {
        didSet { settings.save() }
    }
    var pairing = false

    init(store: any PairingStore = KeychainPairingStore()) {
        self.store = store
        reload()
        network.watch { [weak self] in self?.consoles ?? [] }
    }

    func reload() {
        consoles = store.load().sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    func save(_ console: PairedConsole) throws {
        try store.save(console)
        reload()
    }

    func forget(_ console: PairedConsole) {
        try? store.remove(console)
        reload()
    }

    func reachability(of console: PairedConsole) -> Reachability {
        network.reachability(of: console)
    }
}
