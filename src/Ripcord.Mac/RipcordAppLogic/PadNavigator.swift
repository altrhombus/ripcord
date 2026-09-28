// The library's reading of the pad.

import Foundation
import RipcordKit

/// The library's reading of the pad: edges, not levels. The d-pad or the left stick moves the selection, and
/// cross connects. Repeats while held, the way a key does.
final class PadNavigator: @unchecked Sendable {
    enum Move: Sendable { case up, down, left, right, activate }

    private let state = Locked<(buttons: PadButtons, direction: Move?, heldSince: ContinuousClock.Instant, lastRepeat: ContinuousClock.Instant)>(
        ([], nil, .now, .now))
    private let onMove: @MainActor @Sendable (Move) -> Void
    private let clock: @Sendable () -> ContinuousClock.Instant

    init(clock: @escaping @Sendable () -> ContinuousClock.Instant = { .now },
         onMove: @escaping @MainActor @Sendable (Move) -> Void) {
        self.clock = clock
        self.onMove = onMove
    }

    lazy var sink: @Sendable (PadSnapshot?) -> Void = { [weak self] pad in self?.feed(pad ?? PadSnapshot()) }

    private func feed(_ pad: PadSnapshot) {
        for move in moves(for: pad) { Task { @MainActor in self.onMove(move) } }
    }

    /// The moves one pad state produces, given the ones before it: an edge on cross, a move when the direction
    /// changes, and a repeat every 120 ms once a direction has been held 400 ms.
    func moves(for pad: PadSnapshot) -> [Move] {
        let direction = Self.direction(pad)
        return state.withLock { s -> [Move] in
            var out: [Move] = []
            let now = clock()
            if pad.buttons.contains(.cross) && !s.buttons.contains(.cross) { out.append(.activate) }
            if direction != s.direction {
                s.direction = direction
                s.heldSince = now
                s.lastRepeat = now
                if let direction { out.append(direction) }
            } else if let direction, now - s.heldSince > .milliseconds(400), now - s.lastRepeat > .milliseconds(120) {
                s.lastRepeat = now
                out.append(direction)
            }
            s.buttons = pad.buttons
            return out
        }
    }

    static func direction(_ pad: PadSnapshot) -> Move? {
        if pad.buttons.contains(.dpadUp) { return .up }
        if pad.buttons.contains(.dpadDown) { return .down }
        if pad.buttons.contains(.dpadLeft) { return .left }
        if pad.buttons.contains(.dpadRight) { return .right }
        let s = pad.leftStick
        guard max(abs(s.x), abs(s.y)) > 0.6 else { return nil }
        // GameController's y axis is up-positive, and PadSnapshot keeps it so.
        if abs(s.x) > abs(s.y) { return s.x > 0 ? .right : .left }
        return s.y > 0 ? .up : .down
    }
}
