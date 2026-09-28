// The .NET input rules, ported: the same cases as tests for KeyboardInputTranslator and
// ControllerInputMerger, against the Swift port.

@testable import RipcordKit
import GameController
import Testing

@Suite("Keyboard and merging")
struct InputMergingTests {
    @Test("opposing keys cancel to centre")
    func opposing() {
        var k = KeyboardTranslator()
        k.keyDown(.keyA); k.keyDown(.keyD)
        #expect(k.snapshot.leftStick == .zero)
    }

    @Test("a diagonal is no faster than a straight line")
    func diagonal() {
        var k = KeyboardTranslator()
        k.keyDown(.keyW); k.keyDown(.keyD)
        let s = k.snapshot.leftStick
        #expect(abs((s * s).sum().squareRoot() - 1) < 0.0001)
        #expect(s.x > 0 && s.y > 0)
    }

    @Test("reserved keys cannot be bound, and unbound keys are not input")
    func reserved() {
        var k = KeyboardTranslator(bindings: InputBindings(keyboard: [.escape: .south, .keyX: .east]))
        // Mutating calls stay outside #expect, which may evaluate its argument more than once.
        let escape = k.keyDown(.escape)
        let unbound = k.keyDown(.keyL)
        #expect(!escape && !unbound)
        #expect(!k.hasInput)
        let bound = k.keyDown(.keyX)
        #expect(bound)
        #expect(k.snapshot.buttons == .circle)
    }

    @Test("rebinding drops a held key's old action")
    func rebinding() {
        var k = KeyboardTranslator()
        k.keyDown(.spacebar)
        k.bindings = InputBindings(keyboard: [.keyJ: .south])
        #expect(!k.hasInput)
        #expect(k.snapshot.buttons.isEmpty)
    }

    @Test("triggers from keys are full and pressed")
    func triggers() {
        var k = KeyboardTranslator()
        k.keyDown(.three)
        #expect(k.snapshot.leftTrigger == 1)
        #expect(k.snapshot.buttons.contains(.l2))
    }

    @Test("merging ORs buttons, keeps the further stick and the larger trigger")
    func merge() {
        var a = PadSnapshot(), b = PadSnapshot()
        a.buttons = .cross; b.buttons = .l1
        a.leftStick = SIMD2(0.2, -0.9); b.leftStick = SIMD2(-0.5, 0.3)
        a.rightTrigger = 0.4; b.rightTrigger = 0.7
        let m = PadMerge.merge(a, b)
        #expect(m.buttons == [.cross, .l1])
        #expect(m.leftStick == SIMD2(-0.5, -0.9))
        #expect(m.rightTrigger == 0.7)
        #expect(PadMerge.merge([]) == nil)
    }

    @Test("remapping presses the target instead")
    func remap() {
        var s = PadSnapshot()
        s.buttons = [.cross, .l1]
        let r = PadMerge.applyRemap(s, [.cross: .circle])
        #expect(r.buttons == [.circle, .l1])
    }
}
