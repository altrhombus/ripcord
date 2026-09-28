// Keyboard play, button remapping, and several devices at once: ported from the .NET client's
// Ripcord.Core.Input (KeyboardInputTranslator, InputBindings, ControllerInputMerger), which the Windows
// app ships. The rules are theirs, kept exactly because each one answers a complaint:
//
//   - Opposing keys ADD: left and right held together centre the stick, instead of whichever key a set
//     happened to enumerate last winning and reading as a stuck stick.
//   - Diagonals are scaled onto the unit circle, so two keys are no faster than one.
//   - Merging two devices ORs their buttons, keeps whichever stick is further from centre on each axis,
//     and the larger trigger, which is what lets a person swap pads mid-session without a dead moment.
//   - Escape, F3 and F11 cannot be bound: the app needs them, and a bound Escape would make a full-screen
//     stream inescapable on a keyboard-only Mac (InputBindings.ReservedKeys).
//
// Keys are GameController's GCKeyCode (HID usages), the Mac's equivalent of the Win32 virtual keys the
// .NET defaults are written in. The touchpad click is an action with nowhere to go yet: the core has no
// bit for it, because its code is unconfirmed (PadSnapshot.swift).

import GameController

public enum InputAction: String, Sendable, CaseIterable, Codable {
    case south, east, west, north
    case dpadUp, dpadDown, dpadLeft, dpadRight
    case leftShoulder, rightShoulder, leftStickClick, rightStickClick
    case start, select, guide, touchpadClick
    case leftStickUp, leftStickDown, leftStickLeft, leftStickRight
    case rightStickUp, rightStickDown, rightStickLeft, rightStickRight
    case leftTrigger, rightTrigger

    /// The button an action presses, or nil for the analog ones (and the touchpad, see above).
    var button: PadButtons? {
        switch self {
        case .south: .cross
        case .east: .circle
        case .west: .square
        case .north: .triangle
        case .dpadUp: .dpadUp
        case .dpadDown: .dpadDown
        case .dpadLeft: .dpadLeft
        case .dpadRight: .dpadRight
        case .leftShoulder: .l1
        case .rightShoulder: .r1
        case .leftStickClick: .l3
        case .rightStickClick: .r3
        case .start: .options
        case .select: .create
        case .guide: .ps
        default: nil
        }
    }
}

public struct InputBindings: Sendable, Equatable {
    public var keyboard: [GCKeyCode: InputAction]
    /// A pad button that should press another instead, e.g. swapping cross and circle.
    public var padRemap: [PadButtons: PadButtons]

    public init(keyboard: [GCKeyCode: InputAction] = InputBindings.defaultKeyboard, padRemap: [PadButtons: PadButtons] = [:]) {
        self.keyboard = keyboard.filter { !Self.reservedKeys.contains($0.key) }
        self.padRemap = padRemap
    }

    public static let reservedKeys: Set<GCKeyCode> = [.escape, .F3, .F11]

    /// The .NET defaults (InputBindings.DefaultKeyboard): WASD for the left stick, arrows for the right,
    /// TFGH for the d-pad, and the face buttons on Space/E/Q/R where a right hand reaches them.
    public static let defaultKeyboard: [GCKeyCode: InputAction] = [
        .keyW: .leftStickUp, .keyS: .leftStickDown, .keyA: .leftStickLeft, .keyD: .leftStickRight,
        .upArrow: .rightStickUp, .downArrow: .rightStickDown, .leftArrow: .rightStickLeft, .rightArrow: .rightStickRight,
        .keyT: .dpadUp, .keyG: .dpadDown, .keyF: .dpadLeft, .keyH: .dpadRight,
        .spacebar: .south, .keyE: .east, .keyQ: .west, .keyR: .north,
        .one: .leftShoulder, .two: .rightShoulder, .three: .leftTrigger, .four: .rightTrigger,
        .keyZ: .leftStickClick, .keyC: .rightStickClick,
        .returnOrEnter: .start, .tab: .select, .keyP: .guide, .keyV: .touchpadClick,
    ]
}

/// Held keys to a pad snapshot. Owned by one thread; a front end feeds it key events.
public struct KeyboardTranslator: Sendable {
    public var bindings: InputBindings {
        didSet { held = held.filter { bindings.keyboard[$0] != nil } }   // a rebound key stops driving its old action
    }
    private var held: Set<GCKeyCode> = []

    public init(bindings: InputBindings = InputBindings()) { self.bindings = bindings }

    public var hasInput: Bool { !held.isEmpty }

    /// Returns whether anything changed. Unbound keys are ignored, so they never count as input.
    @discardableResult public mutating func keyDown(_ key: GCKeyCode) -> Bool {
        guard bindings.keyboard[key] != nil else { return false }
        return held.insert(key).inserted
    }

    @discardableResult public mutating func keyUp(_ key: GCKeyCode) -> Bool { held.remove(key) != nil }

    public mutating func clear() { held.removeAll() }

    public var snapshot: PadSnapshot {
        var s = PadSnapshot()
        var left = SIMD2<Float>.zero, right = SIMD2<Float>.zero
        for key in held {
            guard let action = bindings.keyboard[key] else { continue }
            switch action {
            case .leftStickUp: left.y += 1
            case .leftStickDown: left.y -= 1
            case .leftStickLeft: left.x -= 1
            case .leftStickRight: left.x += 1
            case .rightStickUp: right.y += 1
            case .rightStickDown: right.y -= 1
            case .rightStickLeft: right.x -= 1
            case .rightStickRight: right.x += 1
            case .leftTrigger: s.leftTrigger = 1; s.buttons.insert(.l2)
            case .rightTrigger: s.rightTrigger = 1; s.buttons.insert(.r2)
            default: if let b = action.button { s.buttons.insert(b) }
            }
        }
        s.leftStick = Self.unitCircle(left)
        s.rightStick = Self.unitCircle(right)
        return s
    }

    static func unitCircle(_ v: SIMD2<Float>) -> SIMD2<Float> {
        let m = (v * v).sum().squareRoot()
        return m > 1 ? v / m : v
    }
}

public enum PadMerge {
    /// ControllerInputMerger.Merge.
    public static func merge(_ a: PadSnapshot, _ b: PadSnapshot) -> PadSnapshot {
        var m = PadSnapshot()
        m.buttons = a.buttons.union(b.buttons)
        m.leftStick = SIMD2(further(a.leftStick.x, b.leftStick.x), further(a.leftStick.y, b.leftStick.y))
        m.rightStick = SIMD2(further(a.rightStick.x, b.rightStick.x), further(a.rightStick.y, b.rightStick.y))
        m.leftTrigger = max(a.leftTrigger, b.leftTrigger)
        m.rightTrigger = max(a.rightTrigger, b.rightTrigger)
        return m
    }

    /// Every source at once, or nil when none has anything to say.
    public static func merge(_ snapshots: [PadSnapshot]) -> PadSnapshot? {
        snapshots.dropFirst().reduce(snapshots.first) { partial, next in partial.map { merge($0, next) } }
    }

    /// ControllerInputMerger.ApplyRemap: each pressed button presses its remap target instead.
    public static func applyRemap(_ s: PadSnapshot, _ remap: [PadButtons: PadButtons]) -> PadSnapshot {
        guard !remap.isEmpty else { return s }
        var out = s
        out.buttons = []
        for bit in 0..<32 {
            let flag = PadButtons(rawValue: 1 << UInt32(bit))
            guard s.buttons.contains(flag) else { continue }
            out.buttons.insert(remap[flag] ?? flag)
        }
        return out
    }

    private static func further(_ a: Float, _ b: Float) -> Float { abs(a) >= abs(b) ? a : b }
}
