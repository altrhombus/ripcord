// The two conventions that differ between GameController and the wire, and the button mapping, checked
// through a GameController snapshot pad so the reader is exercised too.

@testable import RipcordKit
import GameController
import Testing

@Suite("Pad mapping")
struct PadSnapshotTests {
    @Test("stick up is negative on the wire, and full scale is 32767")
    func stickSign() {
        var s = PadSnapshot()
        s.leftStick = SIMD2(0, 1)          // full up, as GameController reports it
        s.rightStick = SIMD2(-1, -1)       // full left and down
        let w = s.wireState
        #expect(w.left_y == -32767)
        #expect(w.left_x == 0)
        #expect(w.right_x == -32767)
        #expect(w.right_y == 32767)
    }

    @Test("values past the end of the range are clamped")
    func clamping() {
        #expect(PadSnapshot.axis(1.02) == 32767)
        #expect(PadSnapshot.axis(-1.5) == -32767)
        #expect(PadSnapshot.level(1.2) == 255)
        #expect(PadSnapshot.level(-0.1) == 0)
        #expect(PadSnapshot.level(0.5) == 128)
    }

    @Test("a GameController pad maps face buttons by position and shoulders as levels")
    func fromGameController() {
        let controller = GCController.withExtendedGamepad()
        let pad = controller.extendedGamepad!
        pad.buttonA.setValue(1)
        pad.buttonY.setValue(1)
        pad.leftShoulder.setValue(1)
        pad.rightTrigger.setValue(0.75)
        pad.dpad.setValueForXAxis(0, yAxis: 1)   // a snapshot d-pad is set as a pair of axes
        pad.leftThumbstick.yAxis.setValue(1)

        let s = PadSnapshot(pad)
        #expect(s.buttons.contains([.cross, .triangle, .l1, .dpadUp]))
        #expect(s.buttons.contains(.r2), "a trigger at 0.75 is pressed as well as carrying its level")
        #expect(!s.buttons.contains(.circle))
        #expect(s.wireState.right_trigger == 191)
        #expect(s.wireState.left_y == -32767)
    }
}
