// The picture: an AVSampleBufferDisplayLayer, which decodes H.264 and HEVC through VideoToolbox itself and
// presents each frame on arrival (AnnexBSampleBuilder marks every sample DisplayImmediately). PiP and
// HDR come from this layer too (step 7).

import AppKit
import AVFoundation
import SwiftUI

final class VideoSurface: NSView {
    let displayLayer = AVSampleBufferDisplayLayer()

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
