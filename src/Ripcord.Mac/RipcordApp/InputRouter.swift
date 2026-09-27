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
