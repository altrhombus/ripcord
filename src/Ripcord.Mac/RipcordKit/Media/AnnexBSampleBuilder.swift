// From the console's bitstream to what Apple's decoders take.
//
// libripcord's demuxer (stream/stream_demux.c) hands over each video frame as one Annex-B access unit:
// NAL units behind 00 00 01 / 00 00 00 01 start codes, with the parameter sets from STREAM_INFO
// prepended to every keyframe. VideoToolbox and AVSampleBufferDisplayLayer take neither form. They want
// a CMSampleBuffer whose NAL units carry 4-byte big-endian length prefixes (AVCC/HVCC), and a
// CMVideoFormatDescription built from the parameter sets. This file does that conversion, and nothing
// more: no decoding, no timing policy.
//
// The format description is rebuilt only when the parameter sets change, which on a real session means
// a resolution or codec change. Rebuilding it for every keyframe would make the display layer treat each
// one as a new stream.

import CoreMedia
import Foundation

public enum VideoCodec: Sendable {
    case h264, hevc
}

public enum AnnexBError: Error, Sendable, Equatable {
    /// A frame arrived before any parameter sets did, so there is nothing to describe it with.
    case noFormatYet
    /// CoreMedia refused the parameter sets or the sample. The status is CoreMedia's own.
    case coreMedia(OSStatus)
    case emptyFrame
}

public struct AnnexBSampleBuilder {
    public let codec: VideoCodec
    public private(set) var formatDescription: CMVideoFormatDescription?
    private var parameterSets: [Data] = []

    public init(codec: VideoCodec) {
        self.codec = codec
    }

    /// Converts one access unit into a sample buffer. `presentationTime` is the frame's timestamp;
    /// `displayImmediately` marks it for display on arrival, which is what a live stream wants from the
    /// display layer (there is no buffer to wait for).
    public mutating func sampleBuffer(fromAccessUnit accessUnit: UnsafeRawBufferPointer, isKeyframe: Bool,
                                      presentationTime: CMTime,
                                      displayImmediately: Bool = true) throws(AnnexBError) -> CMSampleBuffer {
        var sets: [Data] = []
        var payload = Data()
        payload.reserveCapacity(accessUnit.count + 16)

        for nal in AnnexB.nalUnits(in: accessUnit) {
            guard let header = nal.first else { continue }
            if isParameterSet(header) {
                sets.append(Data(nal))
            } else {
                var length = UInt32(nal.count).bigEndian
                withUnsafeBytes(of: &length) { payload.append(contentsOf: $0) }
                payload.append(contentsOf: nal)
            }
        }

        if !sets.isEmpty && sets != parameterSets {
            formatDescription = try makeFormatDescription(sets)
            parameterSets = sets
        }
        guard let formatDescription else { throw .noFormatYet }
        guard !payload.isEmpty else { throw .emptyFrame }

        let block = try makeBlockBuffer(payload)
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: presentationTime,
                                        decodeTimeStamp: .invalid)
        var size = payload.count
        var sample: CMSampleBuffer?
        let status = CMSampleBufferCreateReady(allocator: kCFAllocatorDefault, dataBuffer: block,
                                               formatDescription: formatDescription, sampleCount: 1,
                                               sampleTimingEntryCount: 1, sampleTimingArray: &timing,
                                               sampleSizeEntryCount: 1, sampleSizeArray: &size,
                                               sampleBufferOut: &sample)
        guard status == noErr, let sample else { throw .coreMedia(status) }

        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true),
           CFArrayGetCount(attachments) > 0 {
            let dictionary = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            if !isKeyframe {
                CFDictionarySetValue(dictionary, Unmanaged.passUnretained(kCMSampleAttachmentKey_NotSync).toOpaque(),
                                     Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
            }
            if displayImmediately {
                CFDictionarySetValue(dictionary, Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                     Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
            }
        }
        return sample
    }

    private func isParameterSet(_ header: UInt8) -> Bool {
        switch codec {
        case .h264:
            let type = header & 0x1F
            return type == 7 || type == 8                    // SPS, PPS
        case .hevc:
            let type = (header >> 1) & 0x3F
            return type == 32 || type == 33 || type == 34    // VPS, SPS, PPS
        }
    }

    private func makeFormatDescription(_ sets: [Data]) throws(AnnexBError) -> CMVideoFormatDescription {
        var description: CMVideoFormatDescription?
        let status: OSStatus = withPointers(to: sets) { pointers, sizes in
            switch codec {
            case .h264:
                CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: kCFAllocatorDefault, parameterSetCount: sets.count,
                    parameterSetPointers: pointers, parameterSetSizes: sizes,
                    nalUnitHeaderLength: 4, formatDescriptionOut: &description)
            case .hevc:
                CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                    allocator: kCFAllocatorDefault, parameterSetCount: sets.count,
                    parameterSetPointers: pointers, parameterSetSizes: sizes,
                    nalUnitHeaderLength: 4, extensions: nil, formatDescriptionOut: &description)
            }
        }
        guard status == noErr, let description else { throw .coreMedia(status) }
        return description
    }

    private func makeBlockBuffer(_ payload: Data) throws(AnnexBError) -> CMBlockBuffer {
        var block: CMBlockBuffer?
        var status = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: payload.count,
            blockAllocator: kCFAllocatorDefault, customBlockSource: nil, offsetToData: 0,
            dataLength: payload.count, flags: kCMBlockBufferAssureMemoryNowFlag, blockBufferOut: &block)
        guard status == noErr, let block else { throw .coreMedia(status) }
        status = payload.withUnsafeBytes {
            CMBlockBufferReplaceDataBytes(with: $0.baseAddress!, blockBuffer: block, offsetIntoDestination: 0,
                                          dataLength: payload.count)
        }
        guard status == noErr else { throw .coreMedia(status) }
        return block
    }
}

/// Keeps every parameter set's bytes alive and contiguous for the duration of one CoreMedia call.
private func withPointers<R>(to sets: [Data],
                             _ body: (UnsafePointer<UnsafePointer<UInt8>>, UnsafePointer<Int>) -> R) -> R {
    let copies = sets.map { data -> UnsafeMutablePointer<UInt8> in
        let p = UnsafeMutablePointer<UInt8>.allocate(capacity: data.count)
        data.copyBytes(to: p, count: data.count)
        return p
    }
    defer { copies.forEach { $0.deallocate() } }
    let pointers = copies.map { UnsafePointer($0) }
    let sizes = sets.map(\.count)
    return pointers.withUnsafeBufferPointer { p in sizes.withUnsafeBufferPointer { s in body(p.baseAddress!, s.baseAddress!) } }
}

public enum AnnexB {
    /// The NAL units in an Annex-B stream, without their start codes. A three-byte start code may
    /// follow a zero byte that belongs to the previous unit's trailing zeros; those are trimmed, since
    /// trailing_zero_8bits are not part of any NAL unit.
    public static func nalUnits(in buffer: UnsafeRawBufferPointer) -> [UnsafeRawBufferPointer.SubSequence] {
        let bytes = buffer
        var units: [UnsafeRawBufferPointer.SubSequence] = []
        var i = 0, start: Int? = nil
        let n = bytes.count
        while i + 2 < n {
            if bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1 {
                if let s = start {
                    var end = i
                    while end > s && bytes[end - 1] == 0 { end -= 1 }
                    if end > s { units.append(bytes[s..<end]) }
                }
                i += 3
                start = i
            } else {
                i += 1
            }
        }
        if let s = start, s < n {
            var end = n
            while end > s && bytes[end - 1] == 0 { end -= 1 }
            if end > s { units.append(bytes[s..<end]) }
        }
        return units
    }
}
