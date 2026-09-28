// Scanning the pairing code off the television (docs/macos-plan.md's Continuity Camera stretch): a camera
// sheet in the pairing flow that reads the eight digits and fills the code field. An iPhone nearby appears as
// a Continuity Camera and is preferred, since a Mac's own camera points at the person rather than the TV.
// Any other camera can be chosen.
//
// Vision's text recognition reads each frame, a few times a second rather than every frame, and
// PairingCodeReader decides what counts as the code. Nothing is sent anywhere: the frames go no further than
// the recogniser.
//
// [X] until it has read a real console's screen: that the code is legible through a phone camera at couch
// distance, and that the recogniser reads it as digits.

// AVCaptureSession predates Sendable; Apple documents starting and stopping it off the main thread, which is
// what the frames queue is for.
@preconcurrency import AVFoundation
import SwiftUI
import Vision

@MainActor
@Observable
final class CodeScanner: NSObject {
    private(set) var cameras: [AVCaptureDevice] = []
    private(set) var selected: AVCaptureDevice?
    private(set) var found: String?
    private(set) var problem: String?

    let session = AVCaptureSession()
    @ObservationIgnored private let reader = Locked(PairingCodeReader())
    @ObservationIgnored private let frames = DispatchQueue(label: "Ripcord code scanner")
    @ObservationIgnored private let lastRead = Locked(ContinuousClock.now - .seconds(1))

    func start() async {
        guard await AVCaptureDevice.requestAccess(for: .video) else {
            problem = String(localized: "Ripcord isn’t allowed to use the camera. You can allow it in System Settings → Privacy & Security → Camera.")
            return
        }
        let discovery = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.continuityCamera, .builtInWideAngleCamera, .external], mediaType: .video, position: .unspecified)
        cameras = discovery.devices
        guard let first = cameras.first(where: { $0.deviceType == .continuityCamera }) ?? cameras.first else {
            problem = String(localized: "No camera is available. An iPhone nearby can be used as one with Continuity Camera.")
            return
        }
        use(first)
    }

    func use(_ camera: AVCaptureDevice) {
        session.beginConfiguration()
        session.inputs.forEach(session.removeInput)
        session.outputs.forEach(session.removeOutput)
        defer { session.commitConfiguration() }
        guard let input = try? AVCaptureDeviceInput(device: camera), session.canAddInput(input) else {
            problem = String(localized: "That camera could not be opened.")
            return
        }
        session.addInput(input)
        let output = AVCaptureVideoDataOutput()
        output.alwaysDiscardsLateVideoFrames = true
        output.setSampleBufferDelegate(self, queue: frames)
        if session.canAddOutput(output) { session.addOutput(output) }
        selected = camera
        problem = nil
        let session = self.session
        frames.async { if !session.isRunning { session.startRunning() } }
    }

    func stop() {
        let session = self.session
        frames.async { session.stopRunning() }
    }

    fileprivate func recognised(_ lines: [String]) {
        guard found == nil, let code = reader.withLock({ $0.observe(lines) }) else { return }
        found = code
        stop()
    }
}

extension CodeScanner: AVCaptureVideoDataOutputSampleBufferDelegate {
    nonisolated func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer,
                                   from connection: AVCaptureConnection) {
        // A few reads a second is plenty for a code that sits on screen, and keeps the recogniser off the
        // camera's frame rate.
        let due = lastRead.withLock { last -> Bool in
            guard ContinuousClock.now - last >= .milliseconds(300) else { return false }
            last = .now
            return true
        }
        guard due, let pixels = sampleBuffer.imageBuffer else { return }
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = false   // digits, not words: correction only gets in the way
        try? VNImageRequestHandler(cvPixelBuffer: pixels).perform([request])
        let lines = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
        Task { @MainActor in self.recognised(lines) }
    }
}

/// The camera's picture, for aiming.
private struct CameraPreview: NSViewRepresentable {
    let session: AVCaptureSession

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        let layer = AVCaptureVideoPreviewLayer(session: session)
        layer.videoGravity = .resizeAspect
        view.layer = layer
        view.wantsLayer = true
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}
}

/// The scanning sheet: point the camera at the code on the television.
struct CodeScannerSheet: View {
    var onCode: (String) -> Void
    @State private var scanner = CodeScanner()
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Scan the Code from the TV").font(.title2.weight(.semibold))
            Text("Point the camera at the code the console shows. An iPhone nearby works as the camera.")
                .foregroundStyle(.secondary)
            ZStack {
                Color.black
                if let problem = scanner.problem {
                    Text(problem).foregroundStyle(.white).padding().multilineTextAlignment(.center)
                } else {
                    CameraPreview(session: scanner.session)
                }
            }
            .frame(height: 260)
            .clipShape(.rect(cornerRadius: 10))
            .accessibilityLabel("Camera preview")
            if scanner.cameras.count > 1 {
                Picker("Camera", selection: Binding(get: { scanner.selected?.uniqueID ?? "" },
                                                    set: { id in scanner.cameras.first { $0.uniqueID == id }.map(scanner.use) })) {
                    ForEach(scanner.cameras, id: \.uniqueID) { Text($0.localizedName).tag($0.uniqueID) }
                }
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(20)
        .frame(width: 440)
        .task { await scanner.start() }
        .onDisappear { scanner.stop() }
        .onChange(of: scanner.found) { _, code in
            guard let code else { return }
            onCode(code)
            dismiss()
        }
    }
}
