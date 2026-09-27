// One InputHub for the whole app, and who its pad goes to.
//
// GameController allows one value handler per controller and one per keyboard, so two hubs would take
// input from each other. The app therefore runs exactly one, and routes its merged pad to the surface that
// owns input right now: the stream window that was last made key, or the library, which reads it as
// navigation. A surface that loses input is sent nil first, so a button held as the focus moved is released
// on the console rather than stuck down.
//
// The keyboard plays only while a stream has captured it (DESIGN.md, "Capture"); the router turns the hub's
// keyboard on and off with it.

import Foundation
import RipcordKit

@MainActor
final class InputRouter {
    typealias Sink = @Sendable (PadSnapshot?) -> Void

    let hub: InputHub
    private let current = Locked<(owner: ObjectIdentifier, sink: Sink)?>(nil)

    init(bindings: InputBindings) {
        hub = InputHub(bindings: bindings)
        hub.keyboardEnabled = false
        let current = self.current
        hub.start { pad in current.withLock { $0?.sink(pad) } }
    }

    /// Gives input to `owner`. The previous owner is sent nil.
    func claim(_ owner: AnyObject, sink: @escaping Sink) {
        let id = ObjectIdentifier(owner)
        let previous = current.withLock { slot -> Sink? in
            defer { slot = (id, sink) }
            return slot?.owner == id ? nil : slot?.sink
        }
        previous?(nil)
    }

    /// Takes input back from `owner`, if it still has it.
    func release(_ owner: AnyObject) {
        let id = ObjectIdentifier(owner)
        let sink = current.withLock { slot -> Sink? in
            guard slot?.owner == id else { return nil }
            defer { slot = nil }
            return slot?.sink
        }
        sink?(nil)
        if sink != nil { hub.keyboardEnabled = false }
    }

    func owns(_ owner: AnyObject) -> Bool {
        current.withLock { $0?.owner == ObjectIdentifier(owner) }
    }
}

/// The library's reading of the pad: edges, not levels. The d-pad or the left stick moves the selection, and
/// cross connects. Repeats while held, the way a key does.
final class PadNavigator: @unchecked Sendable {
    enum Move: Sendable { case up, down, left, right, activate }

    private let state = Locked<(buttons: PadButtons, direction: Move?, heldSince: ContinuousClock.Instant, lastRepeat: ContinuousClock.Instant)>(
        ([], nil, .now, .now))
    private let onMove: @MainActor @Sendable (Move) -> Void

    init(onMove: @escaping @MainActor @Sendable (Move) -> Void) { self.onMove = onMove }

    lazy var sink: InputRouter.Sink = { [weak self] pad in self?.feed(pad ?? PadSnapshot()) }

    private func feed(_ pad: PadSnapshot) {
        let direction = Self.direction(pad)
        let fire = state.withLock { s -> [Move] in
            var out: [Move] = []
            let now = ContinuousClock.now
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
        for move in fire { Task { @MainActor in self.onMove(move) } }
    }

    private static func direction(_ pad: PadSnapshot) -> Move? {
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
