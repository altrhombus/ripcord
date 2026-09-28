// What the network says about the consoles: a search every few seconds, for the library's tile states
// and its Pair tiles.
//
// Each round probes the paired consoles directly, since a network that filters broadcast still passes unicast
// (measured 2026-09-25, journal: "The Mac streams"), then broadcasts briefly for consoles not yet paired. The
// searches block, so they run off the main actor, and only their results come back to it.
//
// SILENCE IS NOT KNOWLEDGE (docs/design.md, "Quiet, not absent"). A probe is one datagram, so a console
// goes to Not found only after two rounds in a row without an answer, and even then its tile keeps the play
// action. Connecting is still worth trying.

import Foundation
import Observation
import RipcordKit

enum Reachability: Equatable {
    /// No round has finished yet. The tile says nothing rather than guess.
    case unknown
    case ready
    /// Rest mode: connecting will wake it. Neutral, never a fault.
    case resting
    case notFound

    var label: String? {
        switch self {
        case .unknown: nil
        case .ready: String(localized: "Ready")
        case .resting: String(localized: "Resting")
        case .notFound: String(localized: "Not found")
        }
    }
}

@MainActor
@Observable
final class NetworkWatch {
    /// Everything the last rounds heard, by host id.
    private(set) var found: [String: DiscoveredConsole] = [:]
    private var misses: [String: Int] = [:]
    private var completedRounds = 0
    private var task: Task<Void, Never>?
    /// A paired console answered from an address its record does not carry.
    @ObservationIgnored var onAddressChange: ((PairedConsole, String) -> Void)?
    /// After each round, so what the widgets show follows the tiles.
    @ObservationIgnored var onRound: () -> Void = {}

    static let interval: Duration = .seconds(5)

    /// One round of searching: the paired hosts in, whatever answered out. The network by default; a test
    /// supplies its own.
    typealias Round = @Sendable ([String]) async -> [DiscoveredConsole]
    @ObservationIgnored private let round: Round

    init(round: @escaping Round = NetworkWatch.searchNetwork) {
        self.round = round
    }

    /// Starts the rounds. `consoles` is read at the start of each, so a console paired meanwhile is included.
    func watch(_ consoles: @escaping @MainActor () -> [PairedConsole]) {
        task?.cancel()
        task = Task { [weak self] in
            while !Task.isCancelled {
                let paired = consoles()
                guard let round = self?.round else { return }
                let heard = await round(paired.map(\.host))
                self?.absorb(heard, paired: paired)
                try? await Task.sleep(for: Self.interval)
            }
        }
    }

    /// One round now, for a Refresh or a pairing sheet that has just opened.
    func refresh(_ paired: [PairedConsole]) async {
        let heard = await round(paired.map(\.host))
        absorb(heard, paired: paired)
    }

    func reachability(of console: PairedConsole) -> Reachability {
        if let heard = answer(for: console) { return heard.isAwake ? .ready : .resting }
        guard completedRounds > 0 else { return .unknown }
        return (misses[key(console)] ?? 0) >= 2 ? .notFound : .unknown
    }

    private func answer(for console: PairedConsole) -> DiscoveredConsole? {
        if !console.consoleID.isEmpty, let byID = found[console.consoleID] { return byID }
        return found.values.first { $0.address == console.host }
    }

    private func key(_ console: PairedConsole) -> String { console.consoleID.isEmpty ? console.host : console.consoleID }

    private func absorb(_ heard: [DiscoveredConsole], paired: [PairedConsole]) {
        var next: [String: DiscoveredConsole] = [:]
        for console in heard { next[console.hostID] = console }
        found = next
        completedRounds += 1
        for console in paired {
            if let answer = answer(for: console) {
                misses[key(console)] = 0
                if answer.address != console.host { onAddressChange?(console, answer.address) }
            } else {
                misses[key(console), default: 0] += 1
            }
        }
        onRound()
    }

    nonisolated static func searchNetwork(hosts: [String]) async -> [DiscoveredConsole] {
        await Task.detached(priority: .utility) {
            var heard: [String: DiscoveredConsole] = [:]
            if !hosts.isEmpty {
                for c in (try? LANDiscovery.search(hosts: hosts, timeout: .milliseconds(1500))) ?? [] { heard[c.hostID] = c }
            }
            for c in (try? LANDiscovery.search(timeout: .milliseconds(1200))) ?? [] where heard[c.hostID] == nil {
                heard[c.hostID] = c
            }
            return Array(heard.values)
        }.value
    }
}
