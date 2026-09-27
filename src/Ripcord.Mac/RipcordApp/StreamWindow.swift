// A stream window (DESIGN.md, "The stream window"): works like QuickTime Player. The picture, aspect-fit on
// black; the connect overlay low and left until it streams; a glass capsule that leaves on its own; the
// inspector (⌘I) as the HUD ladder; native full screen; capture for the keyboard.

import SwiftUI
import RipcordKit

struct StreamWindow: View {
    let target: StreamTarget
    @Environment(AppModel.self) private var model
    @Environment(\.controlActiveState) private var activeState
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.dismissWindow) private var dismissWindow
    @State private var controller: StreamController?
    @State private var digits = ""
    @State private var showControls = true
    @State private var lastPointerMove = ContinuousClock.now
    @State private var window: NSWindow?
    @State private var closeGuard: CloseGuard?
    @State private var wentFullScreen = false
    @State private var confirmDisconnect = false

    var body: some View {
        ZStack {
            Color.black
            if let controller {
                VideoLayerView(surface: controller.surface)
                    .contentShape(.rect)
                    .onTapGesture { if controller.status.isLive { controller.setCapture(true) } }
                    .accessibilityLabel("The picture from \(controller.displayName)")
                overlay(controller)
            } else {
                ContentUnavailableView("This console is no longer paired", systemImage: "questionmark.circle")
            }
        }
        .ignoresSafeArea()
        .onContinuousHover { phase in
            if case .active = phase { pointerMoved() }
        }
        .inspector(isPresented: Binding(get: { controller?.inspectorShown ?? false },
                                        set: { controller?.inspectorShown = $0 })) {
            if let controller {
                InspectorView(controller: controller)
                    .inspectorColumnWidth(min: 260, ideal: Self.inspectorWidth, max: 400)
            }
        }
        .navigationTitle(controller?.displayName ?? "Ripcord")
        .focusedSceneValue(\.stream, controller)
        .background(WindowReader { found in
            guard let found, window !== found else { return }
            window = found
            configure(found)
        })
        .onAppear(perform: start)
        .onDisappear {
            controller?.stop()
            model.streamEnded(target.consoleKey)
        }
        .onChange(of: activeState) { _, state in
            if state == .key { controller?.claimInput() } else { controller?.setCapture(false) }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didResignActiveNotification)) { _ in
            controller?.setCapture(false)
        }
        .onChange(of: controller?.status.isLive ?? false) { _, live in
            if live { enteredStream() }
        }
        .onChange(of: controller?.inspectorShown ?? false) { _, shown in resizeForInspector(shown) }
        .sheet(isPresented: Binding(get: { controller?.passcodeRequest != nil }, set: { _ in })) {
            passcodeSheet
        }
        .confirmationDialog("Disconnect from \(controller?.displayName ?? "")?", isPresented: $confirmDisconnect) {
            Button("Disconnect") { closeGuard?.allowClose(); window?.close() }
            Button("Keep Playing", role: .cancel) {}
        }
        .task(id: lastPointerMove) {
            // The capsule leaves after three seconds of stillness, and the pointer with it.
            try? await Task.sleep(for: .seconds(3))
            guard controller?.status.isLive == true, !Task.isCancelled else { return }
            withAnimation(reduceMotion ? nil : .easeOut(duration: 0.3)) { showControls = false }
            NSCursor.setHiddenUntilMouseMoves(true)
        }
    }

    static let inspectorWidth: CGFloat = 300

    // MARK: Overlays

    @ViewBuilder
    private func overlay(_ c: StreamController) -> some View {
        switch c.status.lifecycle {
        case .streaming, .degraded:
            VStack {
                Spacer()
                if showControls && !c.captured {
                    StreamCapsule(consoleName: c.displayName, recording: c.recording != nil,
                                  canPictureInPicture: c.pip.isPossible,
                                  onPictureInPicture: { c.pip.toggle() },
                                  onRecord: { c.toggleRecording() },
                                  onInspector: { c.inspectorShown.toggle() },
                                  onDisconnect: disconnect)
                        .transition(.opacity)
                } else if let warning = c.warning {
                    // Rung 1: one line in the capsule's place, no numbers.
                    Label(warning.title, systemImage: "exclamationmark.triangle")
                        .font(.callout)
                        .padding(.horizontal, 14).padding(.vertical, 8)
                        .glassEffect(.regular, in: .capsule)
                        .transition(.opacity)
                }
            }
            .padding(.bottom, 28)
            .overlay(alignment: .topTrailing) {
                if let saved = c.lastRecording {
                    RecordingSavedNote(url: saved)
                        .padding(16)
                }
            }
        case .failed, .closed:
            ConnectOverlay(dashes: ConnectProgress.dashes(for: c.stage), line: failureLine(c), detail: failureDetail(c),
                           dimmed: true,
                           actions: AnyView(HStack {
                               Button("Try Again") { c.retry() }.keyboardShortcut(.defaultAction)
                               Button("Close") { closeGuard?.allowClose(); window?.close() }
                           }))
        case .reconnecting:
            ConnectOverlay(dashes: ConnectProgress.dashes(for: c.stage),
                           line: String(localized: "Reconnecting to \(c.displayName)…"), detail: c.status.detail,
                           actions: AnyView(Button("Stop Trying") { c.stopTrying() }))
        case .idle, .connecting:
            ConnectOverlay(dashes: ConnectProgress.dashes(for: c.stage), line: connectingLine(c), detail: nil)
        }
    }

    private func connectingLine(_ c: StreamController) -> String {
        if c.waking && c.stage == .idle { return String(localized: "Waking \(c.displayName)…") }
        return String(localized: "Connecting to \(c.displayName)…")
    }

    private func failureLine(_ c: StreamController) -> String {
        c.status.lifecycle == .closed ? String(localized: "Disconnected") : String(localized: "Couldn’t connect to \(c.displayName)")
    }

    private func failureDetail(_ c: StreamController) -> String? {
        c.status.detail.isEmpty ? nil : c.status.detail
    }

    private var passcodeSheet: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text((controller?.passcodeRequest ?? 0) > 0 ? "That passcode was refused" : "The console is locked")
                .font(.headline)
            Text("Enter the passcode of the console's user.").foregroundStyle(.secondary)
            SecureField("Passcode", text: $digits)
                .textContentType(.password)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { controller?.cancelPasscode() }
                Button("Sign In") {
                    controller?.submitPasscode(digits)
                    digits = ""
                }
                .keyboardShortcut(.defaultAction)
                .disabled(digits.isEmpty)
            }
        }
        .padding(20)
        .frame(width: 340)
    }

    // MARK: Behaviour

    private func start() {
        guard controller == nil, let console = model.console(for: target) else { return }
        let c = StreamController(console: console, settings: model.settings, input: model.input)
        let key = target.consoleKey
        c.onStage = { [model] stage in model.streamStage(key, stage) }
        controller = c
        model.streamStarted(key)
        c.start()
    }

    private func pointerMoved() {
        lastPointerMove = .now
        if !showControls {
            withAnimation(reduceMotion ? nil : .easeOut(duration: 0.15)) { showControls = true }
        }
    }

    private func disconnect() {
        if model.settings.askBeforeDisconnecting { confirmDisconnect = true } else { closeGuard?.allowClose(); window?.close() }
    }

    /// The window as DESIGN.md has it: the stream's aspect while resizing, the launch zoom from the tile,
    /// key handling, and the close guard.
    private func configure(_ window: NSWindow) {
        window.contentAspectRatio = NSSize(width: 16, height: 9)
        window.collectionBehavior.insert(.fullScreenPrimary)
        let guardian = CloseGuard(window: window) { [model] in model.settings.askBeforeDisconnecting }
        guardian.onAsk = { confirmDisconnect = true }
        closeGuard = guardian
        controller?.installMonitors { [weak window] in window }
        controller?.pip.restore = { [weak window] in window?.makeKeyAndOrderFront(nil) }
        if let origin = model.launchOrigins.removeValue(forKey: target.consoleKey) {
            LaunchZoom.run(window, from: origin, reduceMotion: reduceMotion)
        }
    }

    private func enteredStream() {
        guard let window else { return }
        if model.settings.fullScreenOnConnect && !wentFullScreen && !window.styleMask.contains(.fullScreen) {
            wentFullScreen = true
            window.toggleFullScreen(nil)
        }
        pointerMoved()
    }

    /// The inspector claims dead space: the window widens to hold it, so the picture keeps its size. In full
    /// screen there is no dead space left, and it overlays.
    private func resizeForInspector(_ shown: Bool) {
        guard let window, !window.styleMask.contains(.fullScreen) else { return }
        if shown {
            window.contentResizeIncrements = NSSize(width: 1, height: 1)   // clears the aspect lock
        }
        var frame = window.frame
        let delta = shown ? Self.inspectorWidth : -Self.inspectorWidth
        frame.size.width += delta
        if let screen = window.screen?.visibleFrame, frame.maxX > screen.maxX {
            frame.origin.x = max(screen.minX, screen.maxX - frame.width)
        }
        window.setFrame(frame, display: true, animate: !reduceMotion)
        if !shown { window.contentAspectRatio = NSSize(width: 16, height: 9) }
    }
}

/// A moment's confirmation that a recording was saved, with a way to find it.
private struct RecordingSavedNote: View {
    let url: URL
    @State private var visible = true

    var body: some View {
        if visible {
            Button {
                NSWorkspace.shared.activateFileViewerSelecting([url])
            } label: {
                Label("Recording saved", systemImage: "checkmark.circle")
                    .padding(.horizontal, 12).padding(.vertical, 6)
            }
            .buttonStyle(.plain)
            .glassEffect(.regular, in: .capsule)
            .task(id: url) {
                visible = true
                try? await Task.sleep(for: .seconds(4))
                visible = false
            }
        }
    }
}

// MARK: - The window's AppKit parts

/// The launch (DESIGN.md, "The launch"): the stream window grows out of the tile it was opened from. With
/// Reduce Motion it fades in at its own frame instead.
enum LaunchZoom {
    @MainActor
    static func run(_ window: NSWindow, from origin: NSRect, reduceMotion: Bool) {
        let final = window.frame
        if reduceMotion {
            window.alphaValue = 0
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = 0.2
                window.animator().alphaValue = 1
            }
            return
        }
        window.setFrame(origin, display: false)
        window.alphaValue = 0.6
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.32
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            window.animator().setFrame(final, display: true)
            window.animator().alphaValue = 1
        }
    }
}

/// "Ask before disconnecting" for the close button and ⌘W, which reach the window's delegate and nothing
/// else. SwiftUI owns that delegate, so this stands in front of it: it answers windowShouldClose, and passes
/// every other message through to SwiftUI's.
final class CloseGuard: NSObject, NSWindowDelegate {
    private weak var original: NSWindowDelegate?
    private let shouldAsk: @MainActor () -> Bool
    private var allowed = false
    var onAsk: () -> Void = {}

    @MainActor
    init(window: NSWindow, shouldAsk: @escaping @MainActor () -> Bool) {
        original = window.delegate
        self.shouldAsk = shouldAsk
        super.init()
        window.delegate = self
    }

    /// The next close goes through without asking: the person already answered.
    func allowClose() { allowed = true }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if allowed || !MainActor.assumeIsolated(shouldAsk) {
            return original?.windowShouldClose?(sender) ?? true
        }
        onAsk()
        return false
    }

    override func responds(to selector: Selector!) -> Bool {
        super.responds(to: selector) || (original?.responds(to: selector) ?? false)
    }

    override func forwardingTarget(for selector: Selector!) -> Any? {
        original?.responds(to: selector) == true ? original : super.forwardingTarget(for: selector)
    }
}

// MARK: - Menu commands

struct StreamFocusKey: FocusedValueKey {
    typealias Value = StreamController
}

extension FocusedValues {
    var stream: StreamController? {
        get { self[StreamFocusKey.self] }
        set { self[StreamFocusKey.self] = newValue }
    }
}

/// The Stream menu: what the capsule does, where the menu bar can find it.
struct StreamCommands: Commands {
    @FocusedValue(\.stream) private var stream

    var body: some Commands {
        CommandMenu("Stream") {
            Button(stream?.inspectorShown == true ? "Hide Inspector" : "Show Inspector") { stream?.inspectorShown.toggle() }
                .keyboardShortcut("i")
                .disabled(stream == nil)
            Button(stream?.recording != nil ? "Stop Recording" : "Start Recording") { stream?.toggleRecording() }
                .keyboardShortcut("r", modifiers: [.command, .shift])
                .disabled(stream?.status.isLive != true)
            Button(stream?.pip.isActive == true ? "Exit Picture in Picture" : "Enter Picture in Picture") { stream?.pip.toggle() }
                .disabled(stream?.pip.isPossible != true)
            Divider()
            Button("Release Keyboard") { stream?.setCapture(false) }
                .disabled(stream?.captured != true)
            Button("Request a Keyframe") { stream?.requestKeyframe() }
                .disabled(stream?.status.isLive != true)
        }
    }
}
