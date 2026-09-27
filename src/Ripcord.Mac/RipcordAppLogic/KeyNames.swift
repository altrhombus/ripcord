// What the Controls pane calls actions and keys.

import Foundation
import GameController
import RipcordKit

extension InputAction {
    var title: String {
        switch self {
        case .south: String(localized: "Cross")
        case .east: String(localized: "Circle")
        case .west: String(localized: "Square")
        case .north: String(localized: "Triangle")
        case .dpadUp: String(localized: "D-pad up")
        case .dpadDown: String(localized: "D-pad down")
        case .dpadLeft: String(localized: "D-pad left")
        case .dpadRight: String(localized: "D-pad right")
        case .leftShoulder: String(localized: "L1")
        case .rightShoulder: String(localized: "R1")
        case .leftTrigger: String(localized: "L2")
        case .rightTrigger: String(localized: "R2")
        case .leftStickClick: String(localized: "L3")
        case .rightStickClick: String(localized: "R3")
        case .start: String(localized: "Options")
        case .select: String(localized: "Create")
        case .guide: String(localized: "PS button")
        case .touchpadClick: String(localized: "Touchpad")
        case .leftStickUp: String(localized: "Left stick up")
        case .leftStickDown: String(localized: "Left stick down")
        case .leftStickLeft: String(localized: "Left stick left")
        case .leftStickRight: String(localized: "Left stick right")
        case .rightStickUp: String(localized: "Right stick up")
        case .rightStickDown: String(localized: "Right stick down")
        case .rightStickLeft: String(localized: "Right stick left")
        case .rightStickRight: String(localized: "Right stick right")
        }
    }
}

/// Key names by HID usage (the USB HID Usage Tables, keyboard page 0x07), which is what GCKeyCode carries.
enum KeyNames {
    static func name(_ key: GCKeyCode) -> String {
        let usage = key.rawValue
        switch usage {
        case 0x04...0x1D: return String(UnicodeScalar(UInt8(0x41 + usage - 0x04)))
        case 0x1E...0x26: return String(usage - 0x1D)
        case 0x27: return "0"
        case 0x28: return "Return"
        case 0x29: return "Esc"
        case 0x2A: return "Delete"
        case 0x2B: return "Tab"
        case 0x2C: return "Space"
        case 0x2D: return "-"
        case 0x2E: return "="
        case 0x2F: return "["
        case 0x30: return "]"
        case 0x31: return "\\"
        case 0x33: return ";"
        case 0x34: return "'"
        case 0x35: return "`"
        case 0x36: return ","
        case 0x37: return "."
        case 0x38: return "/"
        case 0x3A...0x45: return "F\(usage - 0x39)"
        case 0x4F: return "→"
        case 0x50: return "←"
        case 0x51: return "↓"
        case 0x52: return "↑"
        case 0xE0: return "Left ⌃"
        case 0xE1: return "Left ⇧"
        case 0xE2: return "Left ⌥"
        case 0xE3: return "Left ⌘"
        case 0xE4: return "Right ⌃"
        case 0xE5: return "Right ⇧"
        case 0xE6: return "Right ⌥"
        case 0xE7: return "Right ⌘"
        default: return String(format: "Key 0x%02X", usage)
        }
    }
}
