// AnnexBSampleBuilder, end to end, with no console: VideoToolbox encodes synthetic frames, each access
// unit is re-shaped into what libripcord's demuxer emits (Annex-B, parameter sets prepended to every
// keyframe), and the builder's output is decoded again by VideoToolbox, using the format description the
// builder made. A frame that fails to decode, or decodes at the wrong size, fails the test.

import CoreMedia
import CoreVideo
import Foundation
import RipcordKit
import Synchronization
import Testing
import VideoToolbox

private struct EncodedUnit: Sendable {
    let annexB: Data
    let isKeyframe: Bool
    let pts: CMTime
}

private let startCode: [UInt8] = [0, 0, 0, 1]

/// Encodes `count` frames and returns them as the console would send them.
private func encode(_ codec: VideoCodec, width: Int32, height: Int32, count: Int) throws -> [EncodedUnit] {
    var session: VTCompressionSession?
    let type = codec == .h264 ? kCMVideoCodecType_H264 : kCMVideoCodecType_HEVC
    try #require(VTCompressionSessionCreate(allocator: nil, width: width, height: height, codecType: type,
                                            encoderSpecification: nil, imageBufferAttributes: nil,
                                            compressedDataAllocator: nil, outputCallback: nil, refcon: nil,
                                            compressionSessionOut: &session) == noErr)
    let encoder = try #require(session)
    defer { VTCompressionSessionInvalidate(encoder) }
    VTSessionSetProperty(encoder, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
    VTSessionSetProperty(encoder, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
    VTSessionSetProperty(encoder, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: 4 as CFNumber)

    let units = Mutex<[EncodedUnit]>([])
    for index in 0..<count {
        var pixels: CVPixelBuffer?
        try #require(CVPixelBufferCreate(nil, Int(width), Int(height), kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
                                         [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary, &pixels) == kCVReturnSuccess)
        let frame = try #require(pixels)
        CVPixelBufferLockBaseAddress(frame, [])
        for plane in 0..<CVPixelBufferGetPlaneCount(frame) {
            let base = CVPixelBufferGetBaseAddressOfPlane(frame, plane)!
            let bytes = CVPixelBufferGetBytesPerRowOfPlane(frame, plane) * CVPixelBufferGetHeightOfPlane(frame, plane)
            memset(base, Int32((index * 37 + plane * 90) & 0xFF), bytes)   // a different picture each frame
        }
        CVPixelBufferUnlockBaseAddress(frame, [])

        let pts = CMTime(value: CMTimeValue(index), timescale: 60)
        let status = VTCompressionSessionEncodeFrame(encoder, imageBuffer: frame, presentationTimeStamp: pts,
                                                     duration: .invalid, frameProperties: nil, infoFlagsOut: nil) {
            status, _, sample in
            guard status == noErr, let sample, let unit = consoleShaped(sample, codec: codec) else { return }
            units.withLock { $0.append(unit) }
        }
        try #require(status == noErr)
    }
    VTCompressionSessionCompleteFrames(encoder, untilPresentationTimeStamp: .invalid)
    return units.withLock { $0 }
}

/// An encoder sample (AVCC/HVCC, parameter sets in its format description) as the console's Annex-B.
private func consoleShaped(_ sample: CMSampleBuffer, codec: VideoCodec) -> EncodedUnit? {
    guard let block = CMSampleBufferGetDataBuffer(sample), let format = CMSampleBufferGetFormatDescription(sample) else { return nil }
    let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[CFString: Any]]
    let isKeyframe = !((attachments?.first?[kCMSampleAttachmentKey_NotSync] as? Bool) ?? false)

    var out = Data()
    if isKeyframe {
        var count = 0
        _ = codec == .h264
            ? CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: 0, parameterSetPointerOut: nil,
                                                                parameterSetSizeOut: nil, parameterSetCountOut: &count, nalUnitHeaderLengthOut: nil)
            : CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(format, parameterSetIndex: 0, parameterSetPointerOut: nil,
                                                                parameterSetSizeOut: nil, parameterSetCountOut: &count, nalUnitHeaderLengthOut: nil)
        for i in 0..<count {
            var pointer: UnsafePointer<UInt8>?
            var size = 0
            _ = codec == .h264
                ? CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: i, parameterSetPointerOut: &pointer,
                                                                    parameterSetSizeOut: &size, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
                : CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(format, parameterSetIndex: i, parameterSetPointerOut: &pointer,
                                                                    parameterSetSizeOut: &size, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
            guard let pointer else { return nil }
            out.append(contentsOf: startCode)
            out.append(pointer, count: size)
        }
    }

    var length = 0
    var dataPointer: UnsafeMutablePointer<CChar>?
    guard CMBlockBufferGetDataPointer(block, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &length,
                                      dataPointerOut: &dataPointer) == noErr, let dataPointer else { return nil }
    let avcc = UnsafeRawBufferPointer(start: dataPointer, count: length)
    var offset = 0
    while offset + 4 <= avcc.count {
        let nalLength = Int(avcc.loadUnaligned(fromByteOffset: offset, as: UInt32.self).bigEndian)
        offset += 4
        guard offset + nalLength <= avcc.count else { return nil }
        out.append(contentsOf: startCode)
        out.append(contentsOf: avcc[offset..<(offset + nalLength)])
        offset += nalLength
    }
    return EncodedUnit(annexB: out, isKeyframe: isKeyframe, pts: CMSampleBufferGetPresentationTimeStamp(sample))
}

@Suite("Annex-B to sample buffers")
struct AnnexBSampleBuilderTests {
    @Test("the console's access units decode, at the right size", arguments: [VideoCodec.h264, .hevc])
    func roundTrip(codec: VideoCodec) throws {
        let units = try encode(codec, width: 320, height: 240, count: 12)
        try #require(units.count == 12)
        #expect(units.filter(\.isKeyframe).count >= 2, "the encoder should have produced more than one keyframe")

        var builder = AnnexBSampleBuilder(codec: codec)
        var decoder: VTDecompressionSession?
        let decoded = Mutex<[(Int, Int)]>([])
        defer { if let decoder { VTDecompressionSessionInvalidate(decoder) } }

        for unit in units {
            let sample = try unit.annexB.withUnsafeBytes {
                try builder.sampleBuffer(fromAccessUnit: $0, isKeyframe: unit.isKeyframe, presentationTime: unit.pts)
            }
            if decoder == nil {
                let format = try #require(builder.formatDescription)
                try #require(VTDecompressionSessionCreate(allocator: nil, formatDescription: format, decoderSpecification: nil,
                                                          imageBufferAttributes: nil, outputCallback: nil,
                                                          decompressionSessionOut: &decoder) == noErr)
            }
            let status = VTDecompressionSessionDecodeFrame(try #require(decoder), sampleBuffer: sample, flags: [],
                                                           infoFlagsOut: nil) { status, _, image, _, _ in
                guard status == noErr, let image else { return }
                decoded.withLock { $0.append((CVPixelBufferGetWidth(image), CVPixelBufferGetHeight(image))) }
            }
            #expect(status == noErr)
        }
        VTDecompressionSessionWaitForAsynchronousFrames(try #require(decoder))

        let sizes = decoded.withLock { $0 }
        #expect(sizes.count == units.count)
        #expect(sizes.allSatisfy { $0 == (320, 240) })
    }

    @Test("a frame before any parameter sets has nothing to describe it")
    func noFormatYet() {
        var builder = AnnexBSampleBuilder(codec: .h264)
        let slice: [UInt8] = [0, 0, 0, 1, 0x41, 0x9a, 0x00, 0x10]
        #expect(throws: AnnexBError.noFormatYet) {
            try slice.withUnsafeBytes { try builder.sampleBuffer(fromAccessUnit: $0, isKeyframe: false, presentationTime: .zero) }
        }
    }

    @Test("start codes of both lengths split, and trailing zeros are not part of a unit")
    func nalSplitting() {
        let stream: [UInt8] = [0, 0, 0, 1, 0x67, 0xAA, 0, 0, 1, 0x68, 0xBB, 0, 0, 0, 0, 1, 0x65, 0xCC, 0xDD]
        let units = stream.withUnsafeBytes { AnnexB.nalUnits(in: $0).map { Array($0) } }
        #expect(units == [[0x67, 0xAA], [0x68, 0xBB], [0x65, 0xCC, 0xDD]])
    }
}
