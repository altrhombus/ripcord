// Composes one PNG from the brand sources, for the targets that want pixels rather than vectors: Apple TV's
// layered app icon and Top Shelf images (generate-macos-icon.py runs it). AppKit reads the SVGs, because the Mac
// has no other rasteriser and the project adds no dependency for one.
//
//     xcrun swift brand/compose-png.swift <out.png> <width> <height> [ground] [svg=<path> height=<fraction>]
//
// ground: ripcord-ground.svg's gradient, full-bleed. svg: drawn centred, aspect kept, at that fraction of the
// height. Keep the gradient's two stops in step with ripcord-ground.svg.

import AppKit
var args = Array(CommandLine.arguments.dropFirst())
let out = args.removeFirst(), w = Int(args.removeFirst())!, h = Int(args.removeFirst())!
var ground = false, svg: String?, fraction = 0.5
for a in args {
    if a == "ground" { ground = true }
    else if a.hasPrefix("svg=") { svg = String(a.dropFirst(4)) }
    else if a.hasPrefix("height=") { fraction = Double(a.dropFirst(7))! }
}
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4,
                           hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let bounds = NSRect(x: 0, y: 0, width: w, height: h)
if ground {
    let g = NSGradient(starting: NSColor(srgbRed: 0x30/255, green: 0x37/255, blue: 0x43/255, alpha: 1),
                       ending: NSColor(srgbRed: 0x1B/255, green: 0x1F/255, blue: 0x26/255, alpha: 1))!
    g.draw(in: bounds, angle: -45)   // top-left to bottom-right, as the ground SVG runs
}
if let svg, let image = NSImage(contentsOf: URL(fileURLWithPath: svg)) {
    let dh = Double(h) * fraction, dw = dh * image.size.width / image.size.height
    image.draw(in: NSRect(x: (Double(w) - dw) / 2, y: (Double(h) - dh) / 2, width: dw, height: dh))
}
NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
