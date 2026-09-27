// The mark, drawn: the wedge and three dashes of brand/ripcord-mark-ondark.svg, in its 64-unit box, so it is
// sharp at any size and its dashes can fill one at a time (DESIGN.md, "The launch"). Keep the geometry
// in step with that file; it is the one redrawing of the mark in the Mac tree. The icon is generated from
// the SVG itself, not from this.

import SwiftUI

enum Brand {
    // brand/README.md, "Palette". Our values; never nudge them toward anyone's official colours.
    static let green = Color(red: 0x2F / 255, green: 0xBF / 255, blue: 0x5B / 255)
    static let blue = Color(red: 0x2D / 255, green: 0x7D / 255, blue: 0xF6 / 255)
    static let red = Color(red: 0xF0 / 255, green: 0x43 / 255, blue: 0x3A / 255)
    static let groundTop = Color(red: 0x30 / 255, green: 0x37 / 255, blue: 0x43 / 255)
    static let groundBottom = Color(red: 0x1B / 255, green: 0x1F / 255, blue: 0x26 / 255)

    /// Trail order, top to bottom: which dash is which vendor (brand/README.md, "Which dash is which").
    static let dashes = [green, blue, red]
}

/// The mark's geometry in its 64-unit box.
private enum MarkGeometry {
    static let wedge: [CGPoint] = [CGPoint(x: 29, y: 17), CGPoint(x: 53, y: 32), CGPoint(x: 29, y: 47)]
    static let wedgeStroke: CGFloat = 6
    static let dashes: [CGRect] = [
        CGRect(x: 12, y: 18, width: 9, height: 7),
        CGRect(x: 5, y: 28.5, width: 16, height: 7),
        CGRect(x: 9, y: 39, width: 12, height: 7),
    ]
}

struct RipcordMark: View {
    /// How many dashes are filled, in trail order. 3 is the whole mark.
    var filled = 3
    /// Dimmed: a failed or ended connect. Never red (DESIGN.md, "The launch").
    var dimmed = false
    /// The wedge's colour; white on the dark ground, the system's primary label colour elsewhere.
    var wedge: Color = .white

    var body: some View {
        Canvas { context, size in
            let scale = min(size.width, size.height) / 64
            context.translateBy(x: (size.width - 64 * scale) / 2, y: (size.height - 64 * scale) / 2)
            context.scaleBy(x: scale, y: scale)

            var triangle = Path()
            triangle.addLines(MarkGeometry.wedge)
            triangle.closeSubpath()
            let wedgeShading = GraphicsContext.Shading.color(wedge.opacity(dimmed ? 0.45 : 1))
            context.fill(triangle, with: wedgeShading)
            context.stroke(triangle, with: wedgeShading,
                           style: StrokeStyle(lineWidth: MarkGeometry.wedgeStroke, lineJoin: .round))

            for (i, rect) in MarkGeometry.dashes.enumerated() {
                let pill = Path(roundedRect: rect, cornerRadius: rect.height / 2)
                let colour: Color = i < filled && !dimmed ? Brand.dashes[i] : wedge.opacity(0.18)
                context.fill(pill, with: .color(colour))
            }
        }
        .aspectRatio(1, contentMode: .fit)
        .accessibilityHidden(true)
    }
}

/// The tile's material: the gradient rule (docs/design.md, "Material"). The direction and ratio are ours;
/// the lightness belongs to the appearance, so in light mode the tile is lighter than the window, not a
/// dark hole in it. Increase Contrast flattens it to a system fill.
struct TileGround: View {
    @Environment(\.colorScheme) private var scheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        if contrast == .increased {
            Rectangle().fill(.background.secondary)
        } else if scheme == .dark {
            // The Windows card's values, deeper than the window (docs/design.md, "Material").
            LinearGradient(colors: [Color(red: 0x23 / 255, green: 0x27 / 255, blue: 0x2E / 255),
                                    Color(red: 0x12 / 255, green: 0x15 / 255, blue: 0x1A / 255)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
        } else {
            LinearGradient(colors: [.white, Color(white: 0.94)], startPoint: .topLeading, endPoint: .bottomTrailing)
        }
    }
}

#Preview("The mark, filling") {
    HStack(spacing: 24) {
        ForEach(0..<4) { n in RipcordMark(filled: n).frame(width: 64) }
        RipcordMark(dimmed: true).frame(width: 64)
    }
    .padding(32)
    .background(LinearGradient(colors: [Brand.groundTop, Brand.groundBottom], startPoint: .topLeading,
                               endPoint: .bottomTrailing))
}
