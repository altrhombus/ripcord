// Picture in Picture from the stream's display layer (DESIGN.md, "Picture in Picture and recording"). The
// sample-buffer content source is AVKit's way to float a layer the app feeds itself, which is exactly this
// layer. A live stream has no timeline, so the playback delegate reports an unbounded range, never paused,
// and ignores skips; the floating window then shows no scrubber.

import AVKit
import Observation

@MainActor
@Observable
final class PictureInPicture: NSObject {
    private(set) var isPossible = false
    private(set) var isActive = false
    @ObservationIgnored private var controller: AVPictureInPictureController?
    @ObservationIgnored private var observations: [NSKeyValueObservation] = []
    /// Called when the person asks to return from the floating window.
    @ObservationIgnored var restore: () -> Void = {}

    func attach(_ layer: AVSampleBufferDisplayLayer) {
        guard controller == nil, AVPictureInPictureController.isPictureInPictureSupported() else { return }
        let source = AVPictureInPictureController.ContentSource(sampleBufferDisplayLayer: layer, playbackDelegate: self)
        let controller = AVPictureInPictureController(contentSource: source)
        controller.delegate = self
        controller.requiresLinearPlayback = true
        self.controller = controller
        observations = [
            controller.observe(\.isPictureInPicturePossible, options: [.initial, .new]) { [weak self] c, _ in
                let possible = c.isPictureInPicturePossible
                Task { @MainActor in self?.isPossible = possible }
            },
            controller.observe(\.isPictureInPictureActive, options: [.initial, .new]) { [weak self] c, _ in
                let active = c.isPictureInPictureActive
                Task { @MainActor in self?.isActive = active }
            },
        ]
    }

    func toggle() {
        guard let controller else { return }
        if controller.isPictureInPictureActive { controller.stopPictureInPicture() } else { controller.startPictureInPicture() }
    }

    func stop() {
        controller?.stopPictureInPicture()
    }
}

extension PictureInPicture: AVPictureInPictureControllerDelegate {
    nonisolated func pictureInPictureController(_ controller: AVPictureInPictureController,
                                                restoreUserInterfaceForPictureInPictureStopWithCompletionHandler completion: @escaping (Bool) -> Void) {
        // AVKit calls its delegate on the main thread.
        MainActor.assumeIsolated { restore() }
        completion(true)
    }
}

extension PictureInPicture: AVPictureInPictureSampleBufferPlaybackDelegate {
    nonisolated func pictureInPictureController(_ controller: AVPictureInPictureController, setPlaying playing: Bool) {}

    nonisolated func pictureInPictureControllerTimeRangeForPlayback(_ controller: AVPictureInPictureController) -> CMTimeRange {
        CMTimeRange(start: .negativeInfinity, duration: .positiveInfinity)
    }

    nonisolated func pictureInPictureControllerIsPlaybackPaused(_ controller: AVPictureInPictureController) -> Bool { false }

    nonisolated func pictureInPictureController(_ controller: AVPictureInPictureController,
                                                didTransitionToRenderSize newRenderSize: CMVideoDimensions) {}

    nonisolated func pictureInPictureController(_ controller: AVPictureInPictureController, skipByInterval skipInterval: CMTime,
                                                completion completionHandler: @escaping () -> Void) {
        completionHandler()
    }
}
