// Reading the console's eight-digit pairing code out of text a camera recognised (DESIGN.md, "Pairing": the
// Continuity Camera stretch from docs/macos-plan.md).
//
// What is assumed about the screen is only what the app's own caption says: the console shows an 8-digit code.
// How it groups the digits is not assumed, so a separator (a space, a hyphen, an en dash) may sit between any
// two of them. What is refused is a run of digits longer than eight, since a longer number on screen (a clock,
// a version, a network address) is not the code, and eight digits cut out of it would be a confident wrong
// answer.

import Foundation

struct PairingCodeReader {
    /// How many frames in a row must agree before a code is believed.
    static let agreementNeeded = 2

    private var candidate: String?
    private var agreeing = 0

    /// The code in one frame's recognised lines, or nil when there is none or more than one distinct one.
    static func code(in lines: [String]) -> String? {
        let found = Set(lines.flatMap(codes(in:)))
        return found.count == 1 ? found.first : nil
    }

    /// Every eight-digit code in one line: digits with at most one separator between any two, bounded by
    /// something that is neither a digit nor a separator.
    static func codes(in line: String) -> [String] {
        var out: [String] = []
        var digits = ""
        var pendingSeparator = false
        func close() {
            if digits.count == 8 { out.append(digits) }
            digits = ""
            pendingSeparator = false
        }
        for ch in line {
            if ch.isASCII && ch.isNumber {
                digits.append(ch)
                pendingSeparator = false
            } else if [" ", "-", "\u{2013}", "\u{00A0}"].contains(ch) && !digits.isEmpty && !pendingSeparator {
                pendingSeparator = true
            } else {
                close()
            }
        }
        close()
        return out
    }

    /// Feeds one frame. Returns the code once `agreementNeeded` frames in a row have read the same one.
    mutating func observe(_ lines: [String]) -> String? {
        guard let code = Self.code(in: lines) else {
            candidate = nil
            agreeing = 0
            return nil
        }
        if code == candidate {
            agreeing += 1
        } else {
            candidate = code
            agreeing = 1
        }
        return agreeing >= Self.agreementNeeded ? code : nil
    }
}
