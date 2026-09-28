// The picture's hand-off from the session thread to a display layer's renderer, shared by the Mac and mobile apps.

import AVFoundation

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
