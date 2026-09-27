// One stream window's session: the SessionController that connects and reconnects, the live ConsoleSession
// the pad and the passcode go to, and where its picture and sound are presented.
//
// Threads: the session's handlers run on its own thread; they enqueue video and schedule audio directly
// (both are safe from any thread) and hop to the main actor for anything a view reads.

import AVFoundation
import Observation
import QuartzCore
import RipcordKit

@MainActor
@Observable
final class StreamController {
    private(set) var status = SessionStatus(lifecycle: .idle, detail: "")
    private(set) var stage: SessionStage = .idle
    private(set) var info: SessionStreamInfo?
    private(set) var stats: SessionStats?
    /// Set while the console waits for its user's passcode; `retry` counts refusals.
    private(set) var passcodeRequest: Int?
    /// Engine-to-display latency, as the display link sees frames change (see `LatencyMeter`).
    private(set) var latencyMs: Double?

    let surface = VideoSurface()
    let console: PairedConsole
    private let settings: StreamSettings
    private let live = Locked<ConsoleSession?>(nil)
    private var controller: SessionController?
    private let audio: AudioOutput?
    private let input: InputRouter
    private let meter = LatencyMeter()
    private var displayLink: CADisplayLink?

    init(console: PairedConsole, settings: StreamSettings, input: InputRouter) {
        self.console = console
        self.settings = settings
        self.input = input
        audio = (try? OpusDecoder()).map { AudioOutput(format: $0.outputFormat) }
    }

    func start() {
        guard controller == nil else { return }
        var options = ConsoleSession.Options()
        options.width = settings.width
        options.height = settings.height
        options.fps = settings.fps
        options.bitrateKbps = settings.bitrateKbps
        options.allowHEVC = settings.allowHEVC
        options.hdr = settings.hdr
        options.reportConnectionQuality = settings.reportConnectionQuality

        let video = VideoSink(surface.displayLayer.sampleBufferRenderer)
        let audio = self.audio
        let meter = self.meter
        var handlers = SessionHandlers()
        handlers.video = { sample, _ in
            meter.enqueued()
            video.enqueue(sample)
        }
        handlers.audio = { audio?.schedule($0) }
        handlers.streamInfo = { [weak self] info in Task { @MainActor in self?.info = info } }
        handlers.stats = { [weak self] stats in Task { @MainActor in self?.stats = stats } }
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
        claimInput()
        controller.start()

        let link = surface.displayLink(target: self, selector: #selector(onFrame(_:)))
        link.add(to: .main, forMode: .common)
        displayLink = link
    }

    /// Ends the session. The console rests only when the setting says a person chose that.
    func stop() {
        displayLink?.invalidate()
        displayLink = nil
        input.release(self)
        controller?.stop(restConsole: settings.restConsoleOnDisconnect)
        controller = nil
        audio?.stop()
        surface.displayLayer.sampleBufferRenderer.flush()
    }

    /// Takes the app's pad, when this stream's window becomes key (InputRouter.swift).
    func claimInput() {
        let live = self.live
        input.claim(self) { pad in live.withLock { $0?.update(pad: pad) } }
    }

    func submitPasscode(_ digits: String) {
        passcodeRequest = nil
        live.withLock { $0?.supplyPasscode(digits) }
    }

    func cancelPasscode() {
        passcodeRequest = nil
        live.withLock { $0?.cancel() }
    }

    func requestKeyframe() {
        live.withLock { $0?.requestKeyframe() }
    }

    @objc private func onFrame(_ link: CADisplayLink) {
        let displayed = surface.displayLayer.sampleBufferRenderer.displayedPixelBuffer()
        if let sample = meter.displayed(displayed) {
            latencyMs = sample
        }
    }
}

/// The display layer's renderer, handed to the session thread. AVFoundation documents enqueueing on an
/// AVQueuedSampleBufferRendering from any thread; the type just predates Sendable.
final class VideoSink: @unchecked Sendable {
    private let renderer: AVSampleBufferVideoRenderer

    init(_ renderer: AVSampleBufferVideoRenderer) { self.renderer = renderer }

    func enqueue(_ sample: CMSampleBuffer) {
        // A decode error leaves the renderer failed until flushed; the next keyframe then recovers it.
        if renderer.status == .failed { renderer.flush() }
        renderer.enqueue(sample)
    }
}

/// Engine-to-display latency, measured the only way the layer allows: each enqueue is timestamped, and on
/// every display refresh the displayed pixel buffer is compared with the last one seen. When it changes,
/// the oldest outstanding enqueue is taken as the frame now on screen. The figure is refresh-quantised
/// (up to one display frame late) and approximate under frame drops, and it is the Mac's counterpart of
/// the Windows client's demux-to-present figure, not the same measurement.
final class LatencyMeter: @unchecked Sendable {
    private let pending = Locked<[ContinuousClock.Instant]>([])
    private var lastDisplayed: ObjectIdentifier?
    private var smoothed: Double?

    func enqueued() {
        pending.withLock { p in
            p.append(.now)
            if p.count > 8 { p.removeFirst(p.count - 8) }   // a stalled display must not grow this
        }
    }

    /// Called on the main thread each refresh. Returns a new smoothed figure when a new frame appeared.
    func displayed(_ buffer: CVPixelBuffer?) -> Double? {
        guard let buffer else { return nil }
        let id = ObjectIdentifier(buffer)
        guard id != lastDisplayed else { return nil }
        lastDisplayed = id
        guard let enqueuedAt = pending.withLock({ $0.isEmpty ? nil : $0.removeFirst() }) else { return nil }
        let ms = Double((ContinuousClock.now - enqueuedAt).components.attoseconds) / 1e15
            + Double((ContinuousClock.now - enqueuedAt).components.seconds) * 1000
        smoothed = smoothed.map { $0 * 0.9 + ms * 0.1 } ?? ms
        return smoothed
    }
}
