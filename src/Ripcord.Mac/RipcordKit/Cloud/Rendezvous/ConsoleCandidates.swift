// Which of the console's offered candidates to talk to, decided once for everything that needs to know.
//
// The same choice has to reach two places: the transport that opens the 9303 association
// (ConsolePath.control) and the ACCEPT that names the candidate back to the console. They agreed while a
// candidate parsed and parted company when none did. The ACCEPT named an unparseable candidate (the one
// matching the known host, else the first) while the transport fell back to the known host on 9303, so we
// talked to one address and told the console we had chosen another. It failed safely rather than throwing,
// unlike the dotnet client's copy of the same gap, but it was still two decisions. This is the dotnet client's
// HalyardConsoleCandidates, and the C core and the Rust engine make the choice the same way: one choice, and
// the fallback applied by the caller to both.

import Foundation

enum ConsoleCandidates {
    /// The first candidate on a subnet one of our interfaces shares, so a same-network session stays on the
    /// LAN; else the first that parses, since off-network the console's local address is someone else's
    /// private range and the reflexive one is the only way in. Nil when none parses. "Parses" means the strict
    /// dotted quad and a real port, as ConsolePath reads it, so whatever this returns the transport can reach.
    static func choose(_ candidates: [SignalingCandidate], sharesSubnet: (String) -> Bool) -> SignalingCandidate? {
        var firstParsed: SignalingCandidate?
        for candidate in candidates where ConsolePath(address: candidate.address, port: candidate.port) != nil {
            if sharesSubnet(candidate.address) { return candidate }
            if firstParsed == nil { firstParsed = candidate }
        }
        return firstParsed
    }

    /// The candidate the control association is aimed at and the ACCEPT names, as one decision: the chosen
    /// candidate, or, when none parses, the console's known host on `fallbackPort` for both. Nil when there is
    /// neither.
    ///
    /// The fallback keeps the type of an offered candidate at that address, if there is one, and is otherwise
    /// called `LOCAL`. That the console accepts a candidate it did not offer is [X]: no capture has an offer
    /// with no parseable candidate in it.
    static func resolve(_ candidates: [SignalingCandidate], consoleHost: String, fallbackPort: Int,
                        sharesSubnet: (String) -> Bool) -> SignalingCandidate? {
        if let chosen = choose(candidates, sharesSubnet: sharesSubnet) { return chosen }
        guard ConsolePath(address: consoleHost, port: fallbackPort) != nil else { return nil }
        let type = candidates.first { $0.address == consoleHost }?.type ?? "LOCAL"
        return SignalingCandidate(type: type, address: consoleHost, port: fallbackPort)
    }
}
