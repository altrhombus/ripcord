// Replaces the values in a line of output that identify a network, a device or an account with the
// placeholders docs/README.md lists, so what a harness prints is already safe to paste into a record. The
// Swift counterpart of Ripcord.Diagnostics.IdentifierRedactor, with the same rules; the rationale is there.
//
// By shape: IPv4 addresses, dotted or in the Host header's padded columns, except the loopback, unspecified,
// broadcast, multicast, documentation and synthetic-LAN ones; 19-digit numbers; and hex of 12 or more digits
// with a letter or a separator in it. By value: the names and addresses it has been taught, values it is told
// to deny, and patterns its owner adds. An untaught name of ordinary shape passes, so the harness teaches it
// every name it meets.

import Foundation
import Synchronization

public final class IdentifierRedactor: Sendable {
    private struct State {
        var names: Set<String> = []
        var consoleAddresses: Set<String> = []
        var denied: Set<String> = []
        var patterns: [(NSRegularExpression, String)] = []
        var learned: NSRegularExpression?
        var stale = true
    }

    private let state = Mutex(State())

    private static let address = try! NSRegularExpression(
        pattern: #"(?<![\d.])(\d{1,3})\. {0,2}(\d{1,3})\. {0,2}(\d{1,3})\. {0,2}(\d{1,3})(?![\d.])"#)
    private static let accountShaped = try! NSRegularExpression(pattern: #"(?<!\d)\d{19}(?!\d)"#)
    private static let hexRun = try! NSRegularExpression(
        pattern: #"(?<![0-9A-Za-z])(?:0[xX])?[0-9A-Fa-f](?:[:-]?[0-9A-Fa-f]){11,}(?![0-9A-Za-z])"#)

    public init() {}

    /// A name that identifies real hardware; it becomes `<hostname>`.
    public func learnName(_ name: String?) {
        guard let name = name?.trimmingCharacters(in: .whitespaces), name.count >= 2 else { return }
        state.withLock { if $0.names.insert(name.lowercased()).inserted { $0.stale = true } }
    }

    /// A console's address, so it reads `<console-ip>` rather than `<client-ip>`.
    public func learnConsoleAddress(_ address: String?) {
        guard let address = address?.trimmingCharacters(in: .whitespaces), !address.isEmpty else { return }
        state.withLock { _ = $0.consoleAddresses.insert(address) }
    }

    /// A value that must never appear, whatever its shape; it becomes `<redacted>`.
    public func deny(_ value: String?) {
        guard let value = value?.trimmingCharacters(in: .whitespaces), value.count >= 3 else { return }
        state.withLock { if $0.denied.insert(value.lowercased()).inserted { $0.stale = true } }
    }

    /// An extra pattern and its replacement template, applied before the built-in rules.
    public func addPattern(_ pattern: String, replacement: String) {
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return }
        state.withLock { $0.patterns.append((regex, replacement)) }
    }

    public func redact(_ line: String) -> String {
        guard !line.isEmpty else { return line }
        let (learned, denied, patterns, consoles) = state.withLock { s in
            if s.stale {
                let all = s.names.union(s.denied).sorted { $0.count > $1.count }
                s.learned = all.isEmpty ? nil : try? NSRegularExpression(
                    pattern: #"(?<![\w.-])(?:"# + all.map(NSRegularExpression.escapedPattern(for:)).joined(separator: "|")
                        + #")(?![\w-])"#,
                    options: .caseInsensitive)
                s.stale = false
            }
            return (s.learned, s.denied, s.patterns, s.consoleAddresses)
        }

        var result = line
        if let learned {
            result = Self.replace(learned, in: result) { denied.contains($0.lowercased()) ? "<redacted>" : "<hostname>" }
        }
        for (pattern, replacement) in patterns {
            result = pattern.stringByReplacingMatches(in: result, range: NSRange(result.startIndex..., in: result),
                                                      withTemplate: replacement)
        }
        result = Self.replace(Self.address, in: result) { match in
            let octets = match.split(separator: ".").map { Int($0.trimmingCharacters(in: .whitespaces)) }
            guard octets.count == 4, let o = octets as? [Int], o.allSatisfy({ $0 <= 255 }) else { return match }
            if Self.benign(o) { return match }
            return consoles.contains(o.map(String.init).joined(separator: ".")) ? "<console-ip>" : "<client-ip>"
        }
        result = Self.replace(Self.accountShaped, in: result) { _ in "<redacted>" }
        result = Self.replace(Self.hexRun, in: result) { match in
            match.contains(where: { $0.isLetter || $0 == ":" || $0 == "-" }) ? "<redacted>" : match
        }
        return result
    }

    private static func replace(_ regex: NSRegularExpression, in text: String, with transform: (String) -> String) -> String {
        let matches = regex.matches(in: text, range: NSRange(text.startIndex..., in: text))
        guard !matches.isEmpty else { return text }
        var result = text
        for match in matches.reversed() {
            guard let range = Range(match.range, in: result) else { continue }
            result.replaceSubrange(range, with: transform(String(result[range])))
        }
        return result
    }

    /// Loopback, unspecified, broadcast, multicast, the RFC 5737 ranges and the synthetic LANs (docs/README.md).
    private static func benign(_ o: [Int]) -> Bool {
        switch o[0] {
        case 127, 0: return true
        case 255: return o[1] == 255 && o[2] == 255 && o[3] == 255
        case 224...239: return true
        case 10: return o[1] == 0 && o[2] == 0
        case 172: return o[1] == 31 && o[2] == 0
        case 192: return (o[1] == 168 && o[2] == 1) || (o[1] == 0 && o[2] == 2)
        case 198: return o[1] == 51 && o[2] == 100
        case 203: return o[1] == 0 && o[2] == 113
        default: return false
        }
    }
}
