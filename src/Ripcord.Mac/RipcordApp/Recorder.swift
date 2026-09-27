// Recording a stream to a movie file (DESIGN.md, "Picture in Picture and recording").
//
// The video is the console's own bitstream, appended in passthrough: the samples the display layer gets
// are the samples the file gets, with no re-encode. The file starts at the first keyframe, since a movie
// cannot start mid-GOP. Audio arrives decoded (the stream's Opus has no checked passthrough path into a
// QuickTime movie), so it is encoded to AAC.
//
// Timestamps are the recorder's own, taken from the host clock as each sample arrives, because the stream's
// samples are displayed on arrival and carry no timeline a file can use. Audio is timed by its sample count
// from its first buffer, so it stays gapless.
//
// [X] until a recording has been played back: that passthrough accepts the stream's samples, and what a
// resolution change mid-recording does (the format description changes under a passthrough input).

@preconcurrency import AVFoundation
import CoreMedia

final class Recorder: @unchecked Sendable {
    /// Where recordings go: the Movies folder.
    static var folder: URL { URL.moviesDirectory }

    let url: URL
    // Everything below is touched only on `queue`.
    private let queue = DispatchQueue(label: "Ripcord recording")
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var audioInput: AVAssetWriterInput?
    private var origin: CMTime?
    private var audioOrigin: CMTime?
    private var audioFrames: Int64 = 0
    private var audioFormat: AVAudioFormat?
    private var failed = false

    init(consoleName: String) {
        let stamp = Date.now.formatted(.iso8601.year().month().day().time(includingFractionalSeconds: false)
            .timeSeparator(.omitted).dateTimeSeparator(.space))
        url = Self.folder.appending(path: "\(consoleName) \(stamp).mov")
    }

    private static func now() -> CMTime { CMClockGetTime(CMClockGetHostTimeClock()) }

    /// Called on the session thread.
    func appendVideo(_ sample: CMSampleBuffer, isKeyframe: Bool) {
        let arrived = Self.now()
        let boxed = Handoff(sample)
        queue.async { [self] in
            let sample = boxed.value
            guard !failed else { return }
            if writer == nil {
                guard isKeyframe, let format = sample.formatDescription else { return }
                start(videoFormat: format, at: arrived)
            }
            guard let origin, let input = videoInput, input.isReadyForMoreMediaData else { return }
            var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: arrived - origin,
                                            decodeTimeStamp: .invalid)
            var retimed: CMSampleBuffer?
            CMSampleBufferCreateCopyWithNewTiming(allocator: kCFAllocatorDefault, sampleBuffer: sample,
                                                  sampleTimingEntryCount: 1, sampleTimingArray: &timing,
                                                  sampleBufferOut: &retimed)
            if let retimed, !input.append(retimed) { failed = true }
        }
    }

    /// Called on the session thread. The PCM is copied into a sample buffer here, before the hop.
    func appendAudio(_ buffer: AVAudioPCMBuffer) {
        let arrived = Self.now()
        guard let converted = Self.sampleBuffer(buffer) else { return }
        let format = buffer.format
        let frames = Int64(buffer.frameLength)
        let boxed = Handoff(converted)
        queue.async { [self] in
            let sample = boxed.value
            guard !failed, writer != nil, let origin else { return }
            if audioInput == nil { addAudioInput(format) }
            guard let input = audioInput, input.isReadyForMoreMediaData else { return }
            if audioOrigin == nil { audioOrigin = arrived - origin }
            let pts = audioOrigin! + CMTime(value: audioFrames, timescale: CMTimeScale(format.sampleRate))
            audioFrames += frames
            var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: CMTimeScale(format.sampleRate)),
                                            presentationTimeStamp: pts, decodeTimeStamp: .invalid)
            var retimed: CMSampleBuffer?
            CMSampleBufferCreateCopyWithNewTiming(allocator: kCFAllocatorDefault, sampleBuffer: sample,
                                                  sampleTimingEntryCount: 1, sampleTimingArray: &timing,
                                                  sampleBufferOut: &retimed)
            if let retimed { _ = input.append(retimed) }
        }
    }

    /// Closes the file. Returns its URL, or nil when nothing was written (no keyframe arrived) or it failed.
    func finish() async -> URL? {
        await withCheckedContinuation { continuation in
            queue.async { [self] in
                guard let writer, writer.status == .writing else {
                    continuation.resume(returning: nil)
                    return
                }
                videoInput?.markAsFinished()
                audioInput?.markAsFinished()
                let url = self.url
                let failed = self.failed
                let boxed = Handoff(writer)
                writer.finishWriting {
                    let writer = boxed.value
                    continuation.resume(returning: writer.status == .completed && !failed ? url : nil)
                }
            }
        }
    }

    private func start(videoFormat: CMFormatDescription, at time: CMTime) {
        do {
            let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
            let video = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: videoFormat)
            video.expectsMediaDataInRealTime = true
            guard writer.canAdd(video) else { failed = true; return }
            writer.add(video)
            // The audio input must be added before writing starts; its format is the decoder's, fixed per session.
            if let audioFormat { addAudioInput(audioFormat, to: writer) }
            guard writer.startWriting() else { failed = true; return }
            writer.startSession(atSourceTime: .zero)
            self.writer = writer
            videoInput = video
            origin = time
        } catch {
            failed = true
        }
    }

    /// Tells the recorder the audio format before the first keyframe, so the file gets an audio track.
    func prepareAudio(_ format: AVAudioFormat) {
        queue.async { self.audioFormat = format }
    }

    private func addAudioInput(_ format: AVAudioFormat, to writer: AVAssetWriter? = nil) {
        guard let writer = writer ?? self.writer, audioInput == nil, writer.status == .unknown else { return }
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatMPEG4AAC,
            AVSampleRateKey: format.sampleRate,
            AVNumberOfChannelsKey: format.channelCount,
            AVEncoderBitRateKey: 160_000,
        ]
        let input = AVAssetWriterInput(mediaType: .audio, outputSettings: settings)
        input.expectsMediaDataInRealTime = true
        if writer.canAdd(input) {
            writer.add(input)
            audioInput = input
        }
    }

    private static func sampleBuffer(_ buffer: AVAudioPCMBuffer) -> CMSampleBuffer? {
        var format: CMAudioFormatDescription?
        guard CMAudioFormatDescriptionCreate(allocator: kCFAllocatorDefault, asbd: buffer.format.streamDescription,
                                             layoutSize: 0, layout: nil, magicCookieSize: 0, magicCookie: nil,
                                             extensions: nil, formatDescriptionOut: &format) == noErr,
              let format else { return nil }
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: CMTimeScale(buffer.format.sampleRate)),
                                        presentationTimeStamp: .zero, decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        guard CMSampleBufferCreate(allocator: kCFAllocatorDefault, dataBuffer: nil, dataReady: false,
                                   makeDataReadyCallback: nil, refcon: nil, formatDescription: format,
                                   sampleCount: CMItemCount(buffer.frameLength), sampleTimingEntryCount: 1,
                                   sampleTimingArray: &timing, sampleSizeEntryCount: 0, sampleSizeArray: nil,
                                   sampleBufferOut: &sample) == noErr, let sample else { return nil }
        guard CMSampleBufferSetDataBufferFromAudioBufferList(sample, blockBufferAllocator: kCFAllocatorDefault,
                                                             blockBufferMemoryAllocator: kCFAllocatorDefault, flags: 0,
                                                             bufferList: buffer.audioBufferList) == noErr else { return nil }
        return sample
    }
}

/// A value handed to the recorder's queue by the thread that made it, which then never touches it again.
private struct Handoff<Value>: @unchecked Sendable {
    let value: Value
    init(_ value: Value) { self.value = value }
}
