// A controller's state, in the console's terms, and how a GameController pad becomes one.
//
// The snapshot is a plain value so the mapping to the wire can be tested without a controller attached.
// Two conventions differ between Apple's framework and the console, and both are handled here, once:
//
//   - Stick Y. GameController reports up as positive; the wire has up NEGATIVE (the engine's input
//     writer, ported from libripcord/input/; established in capture cap48, where full up drove left-Y to
//     -32767).
//   - Triggers. GameController gives 0...1; the wire carries a level of 0...255 and a pressed bit.
//
// Face buttons map by POSITION, not by label: GameController's buttonA is the south button on every pad
// (cross on a DualSense, A on an Xbox pad), which is where cross is. The touchpad click is absent on
// purpose: the .NET writer's code for it is unconfirmed by any capture, and the core leaves it out.
//
// This stops at the wire state. When to send it, the history of transitions and the previous frame that
// history is diffed against all belong to the core's connect sequence, which pulls this state; a second
// copy of that cadence here would drift from the one the ports use.

internal import CRipcordEngine
import GameController

public struct PadButtons: OptionSet, Sendable, Hashable {
    public let rawValue: UInt32
    public init(rawValue: UInt32) { self.rawValue = rawValue }

    public static let cross     = PadButtons(rawValue: UInt32(RIPCORD_PAD_CROSS))
    public static let circle    = PadButtons(rawValue: UInt32(RIPCORD_PAD_CIRCLE))
    public static let square    = PadButtons(rawValue: UInt32(RIPCORD_PAD_SQUARE))
    public static let triangle  = PadButtons(rawValue: UInt32(RIPCORD_PAD_TRIANGLE))
    public static let dpadUp    = PadButtons(rawValue: UInt32(RIPCORD_PAD_DPAD_UP))
    public static let dpadDown  = PadButtons(rawValue: UInt32(RIPCORD_PAD_DPAD_DOWN))
    public static let dpadLeft  = PadButtons(rawValue: UInt32(RIPCORD_PAD_DPAD_LEFT))
    public static let dpadRight = PadButtons(rawValue: UInt32(RIPCORD_PAD_DPAD_RIGHT))
    public static let l1        = PadButtons(rawValue: UInt32(RIPCORD_PAD_L1))
    public static let r1        = PadButtons(rawValue: UInt32(RIPCORD_PAD_R1))
    public static let l2        = PadButtons(rawValue: UInt32(RIPCORD_PAD_L2))
    public static let r2        = PadButtons(rawValue: UInt32(RIPCORD_PAD_R2))
    public static let options   = PadButtons(rawValue: UInt32(RIPCORD_PAD_OPTIONS))
    public static let create    = PadButtons(rawValue: UInt32(RIPCORD_PAD_CREATE))
    public static let ps        = PadButtons(rawValue: UInt32(RIPCORD_PAD_PS))
    public static let l3        = PadButtons(rawValue: UInt32(RIPCORD_PAD_L3))
    public static let r3        = PadButtons(rawValue: UInt32(RIPCORD_PAD_R3))
}

/// One instant of a pad, in GameController's own units: sticks -1...1 with up POSITIVE, triggers 0...1.
public struct PadSnapshot: Sendable, Equatable {
    public var buttons: PadButtons = []
    public var leftStick: SIMD2<Float> = .zero
    public var rightStick: SIMD2<Float> = .zero
    public var leftTrigger: Float = 0
    public var rightTrigger: Float = 0

    public init() {}

    /// Reads a GameController pad. Works for any extended gamepad: DualSense, DualShock, Xbox, MFi.
    public init(_ pad: GCExtendedGamepad) {
        var b: PadButtons = []
        if pad.buttonA.isPressed { b.insert(.cross) }
        if pad.buttonB.isPressed { b.insert(.circle) }
        if pad.buttonX.isPressed { b.insert(.square) }
        if pad.buttonY.isPressed { b.insert(.triangle) }
        if pad.dpad.up.isPressed { b.insert(.dpadUp) }
        if pad.dpad.down.isPressed { b.insert(.dpadDown) }
        if pad.dpad.left.isPressed { b.insert(.dpadLeft) }
        if pad.dpad.right.isPressed { b.insert(.dpadRight) }
        if pad.leftShoulder.isPressed { b.insert(.l1) }
        if pad.rightShoulder.isPressed { b.insert(.r1) }
        if pad.leftTrigger.isPressed { b.insert(.l2) }
        if pad.rightTrigger.isPressed { b.insert(.r2) }
        if pad.buttonMenu.isPressed { b.insert(.options) }
        if pad.buttonOptions?.isPressed == true { b.insert(.create) }
        if pad.buttonHome?.isPressed == true { b.insert(.ps) }
        if pad.leftThumbstickButton?.isPressed == true { b.insert(.l3) }
        if pad.rightThumbstickButton?.isPressed == true { b.insert(.r3) }
        buttons = b
        leftStick = SIMD2(pad.leftThumbstick.xAxis.value, pad.leftThumbstick.yAxis.value)
        rightStick = SIMD2(pad.rightThumbstick.xAxis.value, pad.rightThumbstick.yAxis.value)
        leftTrigger = pad.leftTrigger.value
        rightTrigger = pad.rightTrigger.value
    }

    /// The console's wire form of this snapshot.
    var wireState: RipcordInputState {
        var s = RipcordInputState()
        s.buttons = buttons.rawValue
        s.left_x = Self.axis(leftStick.x)
        s.left_y = Self.axis(-leftStick.y)      // up is negative on the wire
        s.right_x = Self.axis(rightStick.x)
        s.right_y = Self.axis(-rightStick.y)
        s.left_trigger = Self.level(leftTrigger)
        s.right_trigger = Self.level(rightTrigger)
        return s
    }

    /// -1...1 to the wire's symmetric -32767...32767, clamped: a pad may report slightly past 1.
    static func axis(_ v: Float) -> Int16 {
        Int16((min(max(v, -1), 1) * 32767).rounded())
    }

    static func level(_ v: Float) -> UInt8 {
        UInt8((min(max(v, 0), 1) * 255).rounded())
    }
}
