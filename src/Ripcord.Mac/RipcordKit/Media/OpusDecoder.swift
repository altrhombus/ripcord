// The console's audio, decoded by Core Audio.
//
// docs/protocol/ps5-remoteplay-v1-spec.md: Opus, 48 kHz, stereo, 480 samples (10 ms) per frame. The
// demuxer emits only each packet's source frame (the redundant copies are for loss concealment and are
// dropped in stream_demux.c), so every call here is exactly one Opus packet. Core Audio has shipped an
// Opus codec for years; AVAudioConverter is the supported way in, and it means no third-party decoder.

import AVFAudio
import Foundation

public enum OpusDecoderError: Error, Sendable {
    case unsupportedFormat
    case converter(String)
}

public final class OpusDecoder {
    public static let sampleRate = 48_000.0
    public static let channels: AVAudioChannelCount = 2
    public static let samplesPerFrame: AVAudioFrameCount = 480

    public let outputFormat: AVAudioFormat
    private let inputFormat: AVAudioFormat
    private let converter: AVAudioConverter

    public init() throws(OpusDecoderError) {
        var description = AudioStreamBasicDescription(
            mSampleRate: Self.sampleRate, mFormatID: kAudioFormatOpus, mFormatFlags: 0, mBytesPerPacket: 0,
            mFramesPerPacket: Self.samplesPerFrame, mBytesPerFrame: 0, mChannelsPerFrame: Self.channels,
            mBitsPerChannel: 0, mReserved: 0)
        guard let input = AVAudioFormat(streamDescription: &description),
              let output = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: Self.sampleRate,
                                         channels: Self.channels, interleaved: false),
              let converter = AVAudioConverter(from: input, to: output)
        else { throw .unsupportedFormat }
        self.inputFormat = input
        self.outputFormat = output
        self.converter = converter
    }

    /// Decodes one Opus packet into 10 ms of PCM.
    ///
    /// **Except the first, which is shorter.** The decoder is stateful across calls, and it trims the
    /// encoder's pre-skip from the start of the stream: measured against Core Audio's own encoder, the
    /// first packet decodes to 360 samples and every one after it to exactly 480. A consumer should queue
    /// what it gets rather than assume 480.
    public func decode(_ packet: UnsafeRawBufferPointer) throws(OpusDecoderError) -> AVAudioPCMBuffer {
        let compressed = AVAudioCompressedBuffer(format: inputFormat, packetCapacity: 1,
                                                 maximumPacketSize: max(packet.count, 1))
        compressed.data.copyMemory(from: packet.baseAddress!, byteCount: packet.count)
        compressed.byteLength = UInt32(packet.count)
        compressed.packetCount = 1
        compressed.packetDescriptions?.pointee = AudioStreamPacketDescription(
            mStartOffset: 0, mVariableFramesInPacket: 0, mDataByteSize: UInt32(packet.count))

        guard let pcm = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: Self.samplesPerFrame) else {
            throw .converter("could not allocate a PCM buffer")
        }
        var supplied = false
        var error: NSError?
        let status = converter.convert(to: pcm, error: &error) { _, outStatus in
            if supplied {
                outStatus.pointee = .noDataNow
                return nil
            }
            supplied = true
            outStatus.pointee = .haveData
            return compressed
        }
        if status == .error { throw .converter(error?.localizedDescription ?? "conversion failed") }
        return pcm
    }
}
