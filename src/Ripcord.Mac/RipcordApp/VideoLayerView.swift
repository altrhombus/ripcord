// The picture: an AVSampleBufferDisplayLayer, which decodes H.264 and HEVC through VideoToolbox itself and
// presents each frame on arrival (AnnexBSampleBuilder marks every sample DisplayImmediately). Picture in
// Picture floats this same layer (PictureInPicture.swift).

import AppKit
import AVFoundation
import SwiftUI

final class VideoSurface: NSView {
    let displayLayer = AVSampleBufferDisplayLayer()

    /// Lets the layer show the stream's extended range on a display that has it. Asked for only when the
    /// stream was asked to be HDR (DESIGN.md, "HDR"). [X] whether the console's HDR metadata reaches the
    /// layer intact: a hardware check.
    var wantsHDR = false {
        didSet { displayLayer.preferredDynamicRange = wantsHDR ? .high : .standard }
    }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer = displayLayer
        displayLayer.videoGravity = .resizeAspect
        displayLayer.backgroundColor = NSColor.black.cgColor
    }

    required init?(coder: NSCoder) { fatalError("not used") }
}

struct VideoLayerView: NSViewRepresentable {
    let surface: VideoSurface

    func makeNSView(context: Context) -> VideoSurface { surface }
    func updateNSView(_ nsView: VideoSurface, context: Context) {}
}
