// One stream window's session: the SessionController that connects and reconnects, the live ConsoleSession
// the pad and the passcode go to, where its picture and sound are presented, and the stream window's own
// state (capture, recording, Picture in Picture, the health verdict).
//
// Threads: the session's handlers run on its own thread; they enqueue video and schedule audio directly
// (both are safe from any thread) and hop to the main actor for anything a view reads.

import AppKit
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
    /// Set while the console waits for its user's passcode; the value counts refusals.
    private(set) var passcodeRequest: Int?
    /// Engine-to-display latency, as the display link sees frames change (see `LatencyMeter`).
    private(set) var latencyMs: Double?
    /// The console was resting and a WAKEUP has gone out; no dash fills until it answers.
    private(set) var waking = false
    /// The last minute of stats intervals, for the inspector's lines.
    private(set) var history: [MetricSample] = []
    private(set) var verdict = StreamHealth.Verdict(severity: .normal, title: "")
    /// Rung 1: shown automatically, cleared automatically, with hysteresis.
    private(set) var warning: StreamHealth.Verdict?
    /// The keyboard plays, and the pointer is hidden (DESIGN.md, "Capture").
    private(set) var captured = false
    private(set) var recording: Recorder?
    /// Where the last recording went, for a moment's confirmation.
    private(set) var lastRecording: URL?
    /// The inspector (⌘I). It returns open or closed as it was left, per window.
    var inspectorShown = false

    let surface = VideoSurface()
    let pip = PictureInPicture()
    let console: PairedConsole
    let settings: StreamSettings
    private let live = Locked<ConsoleSession?>(nil)
    private let recorderSlot = Locked<Recorder?>(nil)
    private var controller: SessionController?
    private let audio: AudioOutput?
    private let audioFormat: AVAudioFormat?
    private let input: InputRouter
    private let meter = LatencyMeter()
    private var displayLink: CADisplayLink?
    private var latch = WarningLatch()
    private var previous: SessionStats?
    private var sampleID = 0
    private var cursorHidden = false
    private var monitors: [Any] = []
    /// Reports stage changes to the app model (the library's "Playing", the menu bar).
    var onStage: (SessionStage) -> Void = { _ in }

    init(console: PairedConsole, settings: StreamSettings, input: InputRouter) {
        self.console = console
        self.settings = settings
        self.input = input
        let decoder = try? OpusDecoder()
        audioFormat = decoder?.outputFormat
        audio = audioFormat.map { AudioOutput(format: $0) }
        pip.attach(surface.displayLayer)
    }

    var displayName: String { console.name.isEmpty ? console.host : console.name }

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
        surface.wantsHDR = settings.hdr

        let video = VideoSink(surface.displayLayer.sampleBufferRenderer)
        let audio = self.audio
        let meter = self.meter
        let recorder = self.recorderSlot
        var handlers = SessionHandlers()
        handlers.video = { sample, keyframe in
            meter.enqueued()
            video.enqueue(sample)
            recorder.withLock { $0 }?.appendVideo(sample, isKeyframe: keyframe)
        }
        handlers.audio = { buffer in
            audio?.schedule(buffer)
            recorder.withLock { $0 }?.appendAudio(buffer)
        }
        handlers.streamInfo = { [weak self] info in Task { @MainActor in self?.info = info } }
        handlers.stats = { [weak self] stats in Task { @MainActor in self?.absorb(stats) } }
        handlers.stage = { [weak self] stage in
            Task { @MainActor in
                guard let self else { return }
                self.stage = stage
                if stage > .idle { self.waking = false }
                self.onStage(stage)
            }
        }
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
        // A resting console is woken first, as the dotnet client does on connect, and the controller starts
        // once it answers. An awake console answers the first probe, so this costs one round trip.
        Task { [weak self] in
            let resting = await Task.detached {
                ((try? LANDiscovery.search(families: [console.family], hosts: [console.host],
                                           timeout: .milliseconds(800))) ?? []).first.map { !$0.isAwake } ?? false
            }.value
            guard let self, self.controller === controller else { return }
            if resting {
                waking = true
                await Task.detached { _ = try? LANWake.wakeIfResting(console) }.value
                guard self.controller === controller else { return }
            }
            controller.start()
        }

        let link = surface.displayLink(target: self, selector: #selector(onFrame(_:)))
        link.add(to: .main, forMode: .common)
        displayLink = link
    }

    /// Ends the session. The console rests only when the setting says a person chose that.
    func stop() {
        setCapture(false)
        removeMonitors()
        stopRecording()
        pip.stop()
        displayLink?.invalidate()
        displayLink = nil
        input.release(self)
        controller?.stop(restConsole: settings.restConsoleOnDisconnect)
        controller = nil
        live.withLock { $0 = nil }
        audio?.stop()
        surface.displayLayer.sampleBufferRenderer.flush()
    }

    /// Try Again after a failure: a fresh SessionController, in the same window.
    func retry() {
        controller?.stop(restConsole: false)
        controller = nil
        displayLink?.invalidate()
        displayLink = nil
        status = SessionStatus(lifecycle: .idle, detail: "")
        stage = .idle
        stats = nil
        previous = nil
        history = []
        latch = WarningLatch()
        warning = nil
        start()
    }

    /// Stop Trying during a reconnect countdown: the session ends, the window stays with the verdict.
    func stopTrying() {
        controller?.stop(restConsole: false)
    }

    /// Takes the app's pad, when this stream's window becomes key (InputRouter.swift). The keyboard follows
    /// capture, not key status.
    func claimInput() {
        let live = self.live
        input.claim(self) { pad in live.withLock { $0?.update(pad: pad) } }
        input.hub.keyboardEnabled = captured
    }

    // MARK: Capture

    func setCapture(_ on: Bool) {
        guard on != captured else { return }
        captured = on
        if on {
            claimInput()
            if !cursorHidden { NSCursor.hide(); cursorHidden = true }
        } else {
            input.hub.keyboardEnabled = false
            if cursorHidden { NSCursor.unhide(); cursorHidden = false }
        }
    }

    /// The window's key handling while it is open: the release chord, swallowing keys the console is
    /// playing (so AppKit does not beep at every one), and Escape leaving full screen when not captured.
    func installMonitors(window: @escaping () -> NSWindow?) {
        guard monitors.isEmpty else { return }
        let release = NSEvent.addLocalMonitorForEvents(matching: .flagsChanged) { [weak self] event in
            let chord: NSEvent.ModifierFlags = [.control, .option]
            if event.modifierFlags.intersection(.deviceIndependentFlagsMask) == chord {
                Task { @MainActor in self?.setCapture(false) }
            }
            return event
        }
        let keys = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .keyUp]) { [weak self] event in
            guard let self, let window = window(), event.window === window else { return event }
            if event.modifierFlags.contains(.command) { return event }   // Mac shortcuts always win
            if MainActor.assumeIsolated({ self.captured }) { return nil }
            if event.type == .keyDown, event.keyCode == 53, window.styleMask.contains(.fullScreen) {   // Escape
                window.toggleFullScreen(nil)
                return nil
            }
            return event
        }
        monitors = [release, keys].compactMap { $0 }
    }

    private func removeMonitors() {
        monitors.forEach(NSEvent.removeMonitor)
        monitors = []
    }

    // MARK: Recording

    func toggleRecording() {
        if recording != nil { stopRecording() } else { startRecording() }
    }

    private func startRecording() {
        let recorder = Recorder(consoleName: displayName)
        if let audioFormat { recorder.prepareAudio(audioFormat) }
        recorderSlot.withLock { $0 = recorder }
        recording = recorder
        requestKeyframe()   // the file starts at a keyframe; ask for one rather than wait for the next
    }

    private func stopRecording() {
        guard let recorder = recording else { return }
        recorderSlot.withLock { $0 = nil }
        recording = nil
        Task {
            lastRecording = await recorder.finish()
        }
    }

    // MARK: The session

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

    private func absorb(_ stats: SessionStats) {
        defer { previous = stats; self.stats = stats }
        guard let previous else { return }
        sampleID += 1
        let sample = MetricSample.between(previous, stats, id: sampleID)
        history.append(sample)
        if history.count > 60 { history.removeFirst(history.count - 60) }
        verdict = StreamHealth.assess(sample, stats: stats, targetFPS: settings.fps)
        latch.feed(verdict)
        warning = latch.shown
    }

    @objc private func onFrame(_ link: CADisplayLink) {
        let displayed = surface.displayLayer.sampleBufferRenderer.displayedPixelBuffer()
        if let sample = meter.displayed(displayed) {
            latencyMs = sample
        }
    }
}

/// Engine-to-display latency, measured the only way the layer allows: each enqueue is timestamped, and on
/// every display refresh the displayed pixel buffer is compared with the last one seen. When it changes,
/// the oldest outstanding enqueue is taken as the frame now on screen. The figure is refresh-quantised
/// (up to one display frame late) and approximate under frame drops, and it is the Mac's counterpart of
/// the dotnet client's demux-to-present figure, not the same measurement.
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
        let elapsed = (ContinuousClock.now - enqueuedAt).components
        let ms = Double(elapsed.seconds) * 1000 + Double(elapsed.attoseconds) / 1e15
        smoothed = smoothed.map { $0 * 0.9 + ms * 0.1 } ?? ms
        return smoothed
    }
}
