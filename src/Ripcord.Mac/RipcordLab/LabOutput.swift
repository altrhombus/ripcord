// Everything ripcord-lab prints goes through an IdentifierRedactor, so a hardware run's output is already safe
// to paste into the journal: console names, addresses, account ids, MACs and keys come out as the placeholders
// docs/README.md lists. --show-identifiers prints them raw, for debugging on your own machine. The sign-in URL
// is raw either way, since it is for opening, not for a record. The counterpart of ProtocolLab's LabOutput.

import Foundation
import RipcordKit

let showIdentifiers = CommandLine.arguments.contains("--show-identifiers")

let labRedactor: IdentifierRedactor = {
    let r = IdentifierRedactor()
    guard !showIdentifiers else { return r }
    r.addPattern(#"\bPS[45]-\d{3}\b"#, replacement: "<hostname>")
    // An address on the command line is always the console's: pair, wake and connect take it.
    for arg in CommandLine.arguments.dropFirst() where arg.split(separator: ".").count == 4 && arg.allSatisfy({ $0.isNumber || $0 == "." }) {
        r.learnConsoleAddress(arg)
    }
    for console in PairingFileStore.lab.load() {
        r.learnName(console.name)
        r.learnConsoleAddress(console.host)
    }
    loadDenylist(into: r)
    return r
}()

/// What a line becomes when printed.
func shown(_ line: String) -> String { showIdentifiers ? line : labRedactor.redact(line) }

/// `print`, redacted. Every line the lab prints goes through here except the sign-in URL.
func say(_ line: String = "", terminator: String = "\n") { print(shown(line), terminator: terminator) }

/// On the owner's machine the leak guard's denylist (tools/leak-guard) names the real values, so the lab redacts
/// those too. The shape rules already cover every hex, account-id and address entry, so only the words are read.
private func loadDenylist(into r: IdentifierRedactor) {
    var dir = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
    while dir.path != "/" {
        let file = dir.appending(path: "docs/protocol/captures/leak-denylist.tsv")
        if let text = try? String(contentsOf: file, encoding: .utf8) {
            for line in text.split(separator: "\n") where !line.hasPrefix("#") {
                let parts = line.split(separator: "\t", maxSplits: 1).map(String.init)
                guard parts.count == 2 else { continue }
                switch parts[0] {
                case "name": r.learnName(parts[1])
                case "ssid", "online-id", "email": r.deny(parts[1])
                case "account-id" where !parts[1].allSatisfy(\.isHexDigit): r.deny(parts[1])
                default: break
                }
            }
            return
        }
        dir = dir.deletingLastPathComponent()
    }
}
