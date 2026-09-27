// Every controller and the keyboard, merged into the one pad the console sees (the .NET client's
// MergedInputSource / CompositeControllerSource, in GameController terms).
//
// GameController objects are not documented as thread-safe, so every callback is delivered on one
// private queue (handlerQueue), each device's latest state is kept there, and the merged snapshot is
// pushed to whoever is listening: a ConsoleSession, whose own lock takes it from there. Nothing reads a
// GCController from the session thread.

import Foundation
// GameController predates Sendable; every object from it is used on `queue` only, which is the guarantee
// the annotations would otherwise have to state.
@preconcurrency import GameController

public final class InputHub: @unchecked Sendable {
    // Everything below is touched only on `queue`.
    private let queue = DispatchQueue(label: "Ripcord input", qos: .userInteractive)
    private var pads: [ObjectIdentifier: PadSnapshot] = [:]
    private var keyboard = KeyboardTranslator()
    private var keyboardOn = true
    private var keyCapture: (@Sendable (GCKeyCode) -> Void)?
    /// ⌘ held: the keys pressed with it are the Mac's shortcuts, never the console's (DESIGN.md, "Capture").
    private var commandHeld = false
    private var observers: [NSObjectProtocol] = []
    private var sink: (@Sendable (PadSnapshot?) -> Void)?
    /// Notifications are delivered straight onto `queue`, so no GameController object crosses a hop.
    private lazy var notificationQueue: OperationQueue = {
        let q = OperationQueue()
        q.underlyingQueue = queue
        q.maxConcurrentOperationCount = 1
        return q
    }()

    public init(bindings: InputBindings = InputBindings()) {
        keyboard = KeyboardTranslator(bindings: bindings)
    }

    /// Starts listening. `sink` receives the merged state whenever it changes, or nil when no device has
    /// anything to say, on the hub's queue.
    public func start(_ sink: @escaping @Sendable (PadSnapshot?) -> Void) {
        queue.async { [self] in
            self.sink = sink
            // Keep receiving controller input when another app is frontmost: a stream in a background
            // window (or Picture in Picture) should still be playable.
            GCController.shouldMonitorBackgroundEvents = true
            let center = NotificationCenter.default
            // Captured strongly: stop() removes the observers, which is what ends the hub's life.
            observers = [
                center.addObserver(forName: .GCControllerDidConnect, object: nil, queue: notificationQueue) { [self] n in
                    if let c = n.object as? GCController { attach(c) }
                },
                center.addObserver(forName: .GCControllerDidDisconnect, object: nil, queue: notificationQueue) { [self] n in
                    guard let c = n.object as? GCController else { return }
                    pads[ObjectIdentifier(c)] = nil
                    publish()
                },
                center.addObserver(forName: .GCKeyboardDidConnect, object: nil, queue: notificationQueue) { [self] _ in
                    attachKeyboard()
                },
            ]
            GCController.controllers().forEach(attach)
            attachKeyboard()
        }
    }

    public func stop() {
        queue.sync {
            observers.forEach(NotificationCenter.default.removeObserver)
            observers = []
            sink = nil
        }
    }

    /// Whether the keyboard plays. Off while the app has not captured it (DESIGN.md, "Capture"): a key
    /// typed into another window must not reach the console. Held keys are released when it turns off.
    public var keyboardEnabled: Bool {
        get { queue.sync { keyboardOn } }
        set {
            queue.async {
                self.keyboardOn = newValue
                if !newValue { self.keyboard.clear() }
                self.publish()
            }
        }
    }

    /// Delivers the next key pressed to `handler` instead of the console, once, for rebinding it. The
    /// hub owns the one keyboard handler GameController allows, so a settings pane asks through here rather
    /// than installing its own and silently taking the keyboard from a stream. Called on the hub's queue.
    public func captureNextKey(_ handler: @escaping @Sendable (GCKeyCode) -> Void) {
        queue.async { self.keyCapture = handler }
    }

    public func cancelKeyCapture() {
        queue.async { self.keyCapture = nil }
    }

    public var bindings: InputBindings {
        get { queue.sync { keyboard.bindings } }
        set { queue.async { self.keyboard.bindings = newValue; self.publish() } }
    }

    private func attach(_ controller: GCController) {
        guard let pad = controller.extendedGamepad else { return }
        controller.handlerQueue = queue
        let id = ObjectIdentifier(controller)
        pads[id] = PadSnapshot(pad)
        pad.valueChangedHandler = { [weak self] pad, _ in
            self?.pads[id] = PadSnapshot(pad)
            self?.publish()
        }
        publish()
    }

    private func attachKeyboard() {
        guard let input = GCKeyboard.coalesced?.keyboardInput else { return }
        GCKeyboard.coalesced?.handlerQueue = queue
        input.keyChangedHandler = { [weak self] _, _, key, pressed in
            guard let self else { return }
            if pressed, let capture = keyCapture {
                keyCapture = nil
                capture(key)
                return
            }
            if key == .leftGUI || key == .rightGUI {
                commandHeld = pressed
                if pressed && keyboard.hasInput {
                    keyboard.clear()
                    publish()
                }
                return
            }
            guard keyboardOn, !commandHeld || !pressed else { return }
            let changed = pressed ? keyboard.keyDown(key) : keyboard.keyUp(key)
            if changed { publish() }
        }
    }

    private func publish() {
        var sources = Array(pads.values)
        if keyboardOn && keyboard.hasInput { sources.append(keyboard.snapshot) }
        sink?(PadMerge.merge(sources).map { PadMerge.applyRemap($0, keyboard.bindings.padRemap) })
    }
}
