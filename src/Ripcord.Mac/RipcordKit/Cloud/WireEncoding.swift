// The three encodings the cloud requests are written in, each reproducing what the .NET reference puts
// on the wire byte for byte.
//
// WHY NOT JSONEncoder. The .NET client is the one that has been shown to work against the service and the
// console, so "the same bytes" is the only safe target, and JSONEncoder cannot produce them:
//
//   - Key order. The connect command's fields are in the captured order (HalyardCloudClient.cs, cap64), and
//     JSONEncoder promises no order at all.
//   - Escaping. System.Text.Json's default encoder writes `"` inside a string as \u0022, and `+ < > & ' ``
//     and everything outside ASCII as \uXXXX. That matters more than it looks: the signaling OFFER is a JSON
//     document carried *inside a string* (`payload = "ver=1.0, type=text, body={...}"`), which PSN relays
//     unparsed and the console parses, so the inner escaping reaches the console as-is. JSONEncoder would
//     write \" and a bare +, and escape `/`.
//
// Both behaviours were measured, not recalled: a scratch program ran the .NET serializer, HttpUtility and
// FormUrlEncodedContent over the awkward characters and the output is what these reproduce. The tests pin
// them to that output.

import Foundation

/// An ordered JSON value, written the way System.Text.Json's default serializer writes it.
indirect enum WireJSON: Sendable, ExpressibleByStringLiteral, ExpressibleByIntegerLiteral {
    case string(String)
    case int(Int)
    case object([(String, WireJSON)])
    case array([WireJSON])
    /// Already-serialized JSON, written verbatim. For the one place the .NET code builds a body by hand.
    case raw(String)

    init(stringLiteral value: String) { self = .string(value) }
    init(integerLiteral value: Int) { self = .int(value) }

    var serialized: String {
        var out = ""
        write(into: &out)
        return out
    }

    private func write(into out: inout String) {
        switch self {
        case .string(let s): out += WireJSON.quoted(s)
        case .int(let i): out += String(i)
        case .raw(let text): out += text
        case .array(let items):
            out += "["
            for (i, item) in items.enumerated() {
                if i > 0 { out += "," }
                item.write(into: &out)
            }
            out += "]"
        case .object(let members):
            out += "{"
            for (i, (key, value)) in members.enumerated() {
                if i > 0 { out += "," }
                out += WireJSON.quoted(key)
                out += ":"
                value.write(into: &out)
            }
            out += "}"
        }
    }

    /// A JSON string literal, escaped as JavaScriptEncoder.Default escapes it: `"` as \u0022, `\` as \\,
    /// the five C escapes, and every other control character, `+ < > & ' `` and DEL, and anything beyond
    /// ASCII (UTF-16, so a surrogate pair is two escapes) as \uXXXX with uppercase hex.
    static func quoted(_ s: String) -> String {
        var out = "\""
        for unit in s.utf16 {
            switch unit {
            case 0x5C: out += "\\\\"
            case 0x0A: out += "\\n"
            case 0x0D: out += "\\r"
            case 0x09: out += "\\t"
            case 0x08: out += "\\b"
            case 0x0C: out += "\\f"
            case 0x22, 0x26, 0x27, 0x2B, 0x3C, 0x3E, 0x60, 0x00..<0x20, 0x7F...:
                out += "\\u" + hex4(unit)
            default:
                out.unicodeScalars.append(Unicode.Scalar(UInt8(unit)))
            }
        }
        return out + "\""
    }

    private static func hex4(_ unit: UInt16) -> String {
        let digits = Array("0123456789ABCDEF")
        return String([digits[Int(unit >> 12)], digits[Int((unit >> 8) & 0xF)],
                       digits[Int((unit >> 4) & 0xF)], digits[Int(unit & 0xF)]])
    }
}

enum WireURLEncoding {
    /// `HttpUtility.ParseQueryString(...).ToString()`, which the .NET authorize URL is built with: letters,
    /// digits and `!*()-._` left alone, a space as `+`, everything else as UTF-8 %xx in *lowercase* hex.
    static func query(_ pairs: [(String, String)]) -> String {
        pairs.map { "\(encode($0.0, unreserved: queryUnreserved, lowercase: true))=\(encode($0.1, unreserved: queryUnreserved, lowercase: true))" }
            .joined(separator: "&")
    }

    /// `FormUrlEncodedContent`, which the token requests are sent as: letters, digits and `-._~` left
    /// alone, a space as `+`, everything else as UTF-8 %XX in *uppercase* hex. Not the same set as the
    /// query encoding above, and not the same case: both measured.
    static func form(_ pairs: [(String, String)]) -> String {
        pairs.map { "\(encode($0.0, unreserved: formUnreserved, lowercase: false))=\(encode($0.1, unreserved: formUnreserved, lowercase: false))" }
            .joined(separator: "&")
    }

    private static let queryUnreserved = Set("!*()-._".utf8)
    private static let formUnreserved = Set("-._~".utf8)

    private static func encode(_ s: String, unreserved: Set<UInt8>, lowercase: Bool) -> String {
        let digits = Array(lowercase ? "0123456789abcdef" : "0123456789ABCDEF")
        var out = ""
        for byte in s.utf8 {
            switch byte {
            case UInt8(ascii: "a")...UInt8(ascii: "z"), UInt8(ascii: "A")...UInt8(ascii: "Z"),
                 UInt8(ascii: "0")...UInt8(ascii: "9"):
                out.unicodeScalars.append(Unicode.Scalar(byte))
            case UInt8(ascii: " "):
                out += "+"
            case let b where unreserved.contains(b):
                out.unicodeScalars.append(Unicode.Scalar(byte))
            default:
                out += "%" + String([digits[Int(byte >> 4)], digits[Int(byte & 0xF)]])
            }
        }
        return out
    }
}

extension Array where Element == UInt8 {
    /// Lowercase hex, as `Convert.ToHexString(...).ToLowerInvariant()` writes it.
    var lowercaseHex: String {
        let digits = [Character]("0123456789abcdef")
        var out = ""
        out.reserveCapacity(count * 2)
        for byte in self {
            out.append(digits[Int(byte >> 4)])
            out.append(digits[Int(byte & 0xF)])
        }
        return out
    }
}
