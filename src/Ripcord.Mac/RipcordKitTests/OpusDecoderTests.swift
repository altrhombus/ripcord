// OpusDecoder against Core Audio's own Opus encoder: a tone goes in as 10 ms packets in the console's
// format, and each packet must come back as 480 stereo samples carrying the tone rather than silence.

import AVFAudio
import Foundation
import RipcordKit
import Testing

@Suite("Opus")
struct OpusDecoderTests {
    @Test("10 ms packets in the console's format decode to 480 stereo samples")
    func roundTrip() throws {
        let pcmFormat = try #require(AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 2, interleaved: false))
        var opusDescription = AudioStreamBasicDescription(
            mSampleRate: 48_000, mFormatID: kAudioFormatOpus, mFormatFlags: 0, mBytesPerPacket: 0,
            mFramesPerPacket: 480, mBytesPerFrame: 0, mChannelsPerFrame: 2, mBitsPerChannel: 0, mReserved: 0)
        let opusFormat = try #require(AVAudioFormat(streamDescription: &opusDescription))
        let encoder = try #require(AVAudioConverter(from: pcmFormat, to: opusFormat), "no Core Audio Opus encoder")

        // Half a second of a 440 Hz tone.
        let total: AVAudioFrameCount = 24_000
        let tone = try #require(AVAudioPCMBuffer(pcmFormat: pcmFormat, frameCapacity: total))
        tone.frameLength = total
        for channel in 0..<2 {
            let samples = tone.floatChannelData![channel]
            for i in 0..<Int(total) { samples[i] = 0.5 * sinf(2 * .pi * 440 * Float(i) / 48_000) }
        }

        var packets: [Data] = []
        var consumed = false
        while true {
            let out = AVAudioCompressedBuffer(format: opusFormat, packetCapacity: 8, maximumPacketSize: encoder.maximumOutputPacketSize)
            var error: NSError?
            let status = encoder.convert(to: out, error: &error) { _, outStatus in
                if consumed { outStatus.pointee = .endOfStream; return nil }
                consumed = true
                outStatus.pointee = .haveData
                return tone
            }
            try #require(status != .error, "encode failed: \(String(describing: error))")
            for p in 0..<Int(out.packetCount) {
                let d = out.packetDescriptions![p]
                packets.append(Data(bytes: out.data + Int(d.mStartOffset), count: Int(d.mDataByteSize)))
            }
            if status == .endOfStream || out.packetCount == 0 { break }
        }
        try #require(packets.count >= 40, "expected about 50 packets of 10 ms, got \(packets.count)")

        let decoder = try OpusDecoder()
        var energy: Float = 0
        var lengths: [AVAudioFrameCount] = []
        for packet in packets {
            let pcm = try packet.withUnsafeBytes { try decoder.decode($0) }
            lengths.append(pcm.frameLength)
            #expect(pcm.format.channelCount == 2)
            for i in 0..<Int(pcm.frameLength) { energy += abs(pcm.floatChannelData![0][i]) }
        }
        #expect(lengths.dropFirst().allSatisfy { $0 == 480 }, "lengths: \(lengths)")
        #expect(energy > 100, "the decoded audio is silent")
    }
}
