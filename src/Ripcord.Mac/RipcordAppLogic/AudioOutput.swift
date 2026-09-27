// Sound: the session's 10 ms PCM buffers into an AVAudioPlayerNode, for the Mac and mobile apps alike. A stream has no timeline to honour, so
// the queue is kept short: past about 60 ms queued, a buffer is dropped rather than letting the audio fall
// behind the picture for good.

import AVFAudio

final class AudioOutput: @unchecked Sendable {
    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let queued = Locked(0)
    private let maxQueued = 6
    private var started = false

    init(format: AVAudioFormat) {
        engine.attach(player)
        engine.connect(player, to: engine.mainMixerNode, format: format)
    }

    func start() {
        guard !started else { return }
        do {
            try engine.start()
            player.play()
            started = true
        } catch {
            started = false   // no audio device: the stream goes on without sound
        }
    }

    /// Called on the session thread.
    func schedule(_ buffer: AVAudioPCMBuffer) {
        guard started else { return }
        let admit = queued.withLock { n -> Bool in
            guard n < maxQueued else { return false }
            n += 1
            return true
        }
        guard admit else { return }
        player.scheduleBuffer(buffer) { [queued] in queued.withLock { $0 -= 1 } }
    }

    func stop() {
        player.stop()
        engine.stop()
        started = false
    }
}
