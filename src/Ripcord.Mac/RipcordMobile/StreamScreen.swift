// A stream: the picture, sound, a controller, and the console's passcode prompt when it asks. The Mac's
// StreamController, reduced to what the scaffolding needs. No touch controls yet: a controller is required,
// which on iPhone and iPad means a paired Bluetooth one and on Apple TV the one it already has (docs/ios-plan.md).

import AVFoundation
import SwiftUI
import UIKit
import RipcordKit

@MainActor
@Observable
final class MobileStream {
    private(set) var status = SessionStatus(lifecycle: .idle, detail: "")
    private(set) var stage: SessionStage = .idle
    private(set) var passcodeRequest: Int?

    let layer = AVSampleBufferDisplayLayer()
    let console: PairedConsole
    private let settings: StreamSettings
    private let live = Locked<ConsoleSession?>(nil)
    private var controller: SessionController?
    private let audio: AudioOutput?
    private let input = InputHub()

    init(console: PairedConsole, settings: StreamSettings) {
        self.console = console
        self.settings = settings
        audio = (try? OpusDecoder()).map { AudioOutput(format: $0.outputFormat) }
        layer.videoGravity = .resizeAspect
    }

    var displayName: String { console.name.isEmpty ? console.host : console.name }

    func start() {
        guard controller == nil else { return }
        // Playback, so the stream's sound plays with the silent switch on and mixes as a game's does. The session's
        // other options (ducking, AirPlay routing) are open (docs/ios-plan.md).
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .default)
        try? AVAudioSession.sharedInstance().setActive(true)
        UIApplication.shared.isIdleTimerDisabled = true

        var options = ConsoleSession.Options()
        options.width = settings.width
        options.height = settings.height
        options.fps = settings.fps
        options.bitrateKbps = settings.bitrateKbps
        options.allowHEVC = settings.allowHEVC

        let video = VideoSink(layer.sampleBufferRenderer)
        let audio = self.audio
        var handlers = SessionHandlers()
        handlers.video = { sample, _ in video.enqueue(sample) }
        handlers.audio = { audio?.schedule($0) }
        handlers.stage = { [weak self] stage in Task { @MainActor in self?.stage = stage } }
        handlers.passcodeRequested = { [weak self] retry in Task { @MainActor in self?.passcodeRequest = retry } }

        let console = self.console
        let live = self.live
        let sessionOptions = options
        let controller = SessionController(handlers: handlers, onStatus: { [weak self] status in
            Task { @MainActor in self?.status = status }
        }, factory: { handlers in
            let session = ConsoleSession(console: console, options: sessionOptions, handlers: handlers)
            live.withLock { $0 = session }
            return session
        })
        self.controller = controller
        audio?.start()
        input.start { [live] pad in live.withLock { $0?.update(pad: pad) } }
        // A resting console is woken first, as the Mac and the dotnet client do.
        Task {
            _ = await Task.detached { try? LANWake.wakeIfResting(console) }.value
            controller.start()
        }
    }

    func stop() {
        input.stop()
        controller?.stop(restConsole: settings.restConsoleOnDisconnect)
        controller = nil
        audio?.stop()
        layer.sampleBufferRenderer.flush()
        UIApplication.shared.isIdleTimerDisabled = false
    }

    func submitPasscode(_ digits: String) {
        passcodeRequest = nil
        live.withLock { $0?.supplyPasscode(digits) }
    }

    func cancelPasscode() {
        passcodeRequest = nil
        live.withLock { $0?.cancel() }
    }
}

/// The display layer, in a view UIKit sizes.
private final class PictureView: UIView {
    let picture: AVSampleBufferDisplayLayer

    init(_ picture: AVSampleBufferDisplayLayer) {
        self.picture = picture
        super.init(frame: .zero)
        backgroundColor = .black
        layer.addSublayer(picture)
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override func layoutSubviews() {
        super.layoutSubviews()
        picture.frame = bounds
    }
}

private struct Picture: UIViewRepresentable {
    let layer: AVSampleBufferDisplayLayer
    func makeUIView(context: Context) -> PictureView { PictureView(layer) }
    func updateUIView(_ view: PictureView, context: Context) {}
}

struct StreamScreen: View {
    @State private var stream: MobileStream
    @State private var digits = ""
    @Environment(\.dismiss) private var dismiss

    init(console: PairedConsole, settings: StreamSettings) {
        _stream = State(initialValue: MobileStream(console: console, settings: settings))
    }

    var body: some View {
        ZStack(alignment: .bottomLeading) {
            Picture(layer: stream.layer).ignoresSafeArea()
            if !stream.status.isLive {
                VStack(alignment: .leading, spacing: 6) {
                    Text(line).font(.headline).foregroundStyle(.white)
                    if !stream.status.detail.isEmpty {
                        Text(stream.status.detail).font(.callout).foregroundStyle(.white.opacity(0.7))
                    }
                }
                .padding(28)
            }
        }
        .overlay(alignment: .topTrailing) {
            Button("Disconnect", systemImage: "xmark.circle.fill") { dismiss() }
                .labelStyle(.iconOnly)
                .font(.title)
                .padding()
        }
        #if os(iOS)
        .statusBarHidden()
        .persistentSystemOverlays(.hidden)
        #endif
        .onAppear { stream.start() }
        .onDisappear { stream.stop() }
        .alert("The console is locked", isPresented: Binding(get: { stream.passcodeRequest != nil }, set: { _ in })) {
            SecureField("Passcode", text: $digits)
            Button("Sign In") { stream.submitPasscode(digits); digits = "" }
            Button("Cancel", role: .cancel) { stream.cancelPasscode() }
        } message: {
            Text((stream.passcodeRequest ?? 0) > 0 ? "That passcode was refused. Enter the passcode of the console's user."
                                                    : "Enter the passcode of the console's user.")
        }
    }

    private var line: String {
        switch stream.status.lifecycle {
        case .failed: String(localized: "Couldn’t connect to \(stream.displayName)")
        case .reconnecting: String(localized: "Reconnecting to \(stream.displayName)…")
        case .closed: String(localized: "Disconnected")
        default: String(localized: "Connecting to \(stream.displayName)…")
        }
    }
}
