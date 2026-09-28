// Moving around the library: the grid's column count, arrow and pad moves, and how the pad is read.

import CoreGraphics
import Foundation
import RipcordKit
import Synchronization
import Testing

@Suite("Library navigation")
struct NavigationTests {
    @Test("the column count matches GridItem.adaptive's arithmetic")
    func columns() {
        // Two 250-point tiles and a 16-point gap need 516 points, plus 20 of padding either side.
        #expect(GridNavigation.columns(width: 556, minimum: 250, spacing: 16, padding: 20) == 2)
        #expect(GridNavigation.columns(width: 555, minimum: 250, spacing: 16, padding: 20) == 1)
        #expect(GridNavigation.columns(width: 100, minimum: 250, spacing: 16, padding: 20) == 1)   // never zero
    }

    @Test("moves stay inside the grid rather than wrapping")
    func moves() {
        // Five tiles in rows of three: 0 1 2 / 3 4
        #expect(GridNavigation.next(from: 0, count: 5, columns: 3, move: .right) == 1)
        #expect(GridNavigation.next(from: 1, count: 5, columns: 3, move: .down) == 4)
        #expect(GridNavigation.next(from: 2, count: 5, columns: 3, move: .down) == nil)    // no tile under it
        #expect(GridNavigation.next(from: 0, count: 5, columns: 3, move: .left) == nil)
        #expect(GridNavigation.next(from: 4, count: 5, columns: 3, move: .right) == nil)
        #expect(GridNavigation.next(from: 3, count: 5, columns: 3, move: .up) == 0)
        #expect(GridNavigation.next(from: 3, count: 5, columns: 3, move: .activate) == nil)
    }

    static func pad(_ buttons: PadButtons = [], stick: SIMD2<Float> = .zero) -> PadSnapshot {
        var pad = PadSnapshot()
        pad.buttons = buttons
        pad.leftStick = stick
        return pad
    }

    @Test("the d-pad and a firm push of the left stick are directions; a light touch is not")
    func directions() {
        #expect(PadNavigator.direction(Self.pad(.dpadUp)) == .up)
        #expect(PadNavigator.direction(Self.pad(stick: [0, 0.9])) == .up)        // y is up-positive
        #expect(PadNavigator.direction(Self.pad(stick: [0, -0.9])) == .down)
        #expect(PadNavigator.direction(Self.pad(stick: [0.9, 0.5])) == .right)   // the stronger axis wins
        #expect(PadNavigator.direction(Self.pad(stick: [0.5, 0.5])) == nil)      // under the 0.6 dead zone
    }

    @Test("cross fires on its edge, a direction on its change, and a held direction repeats")
    func edgesAndRepeat() {
        let now = Mutex(ContinuousClock.now)
        let navigator = PadNavigator(clock: { now.withLock { $0 } }, onMove: { _ in })
        func advance(_ ms: Int) { now.withLock { $0 = $0 + .milliseconds(ms) } }

        #expect(navigator.moves(for: Self.pad(.cross)) == [.activate])
        #expect(navigator.moves(for: Self.pad(.cross)).isEmpty)              // held, not pressed again
        #expect(navigator.moves(for: Self.pad()).isEmpty)

        #expect(navigator.moves(for: Self.pad(.dpadRight)) == [.right])
        advance(300)
        #expect(navigator.moves(for: Self.pad(.dpadRight)).isEmpty)          // not held long enough to repeat
        advance(150)
        #expect(navigator.moves(for: Self.pad(.dpadRight)) == [.right])      // 450 ms held: repeats
        advance(50)
        #expect(navigator.moves(for: Self.pad(.dpadRight)).isEmpty)          // under the 120 ms repeat interval
        advance(100)
        #expect(navigator.moves(for: Self.pad(.dpadRight)) == [.right])
        #expect(navigator.moves(for: Self.pad(.dpadDown)) == [.down])        // a change fires at once
    }
}
