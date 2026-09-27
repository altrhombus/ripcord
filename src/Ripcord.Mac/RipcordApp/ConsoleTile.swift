// A console tile (DESIGN.md, "The tile"): the name, the plain-text family label, the state in words, and the
// wedge at the trailing edge as the tile's action.

import SwiftUI
import RipcordKit

/// The trailing wedge zone: a slanted facet, the dotnet client's card action language (docs/design.md, "Form").
struct WedgeFacet: Shape {
    /// How far in from the leading edge the slant starts, as a fraction of the height.
    var slant: CGFloat = 0.32

    func path(in rect: CGRect) -> Path {
        var p = Path()
        p.move(to: CGPoint(x: rect.minX + rect.height * slant, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY))
        p.addLine(to: CGPoint(x: rect.minX, y: rect.maxY))
        p.closeSubpath()
        return p
    }
}

private struct PlayGlyph: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        p.move(to: CGPoint(x: rect.minX, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.midY))
        p.addLine(to: CGPoint(x: rect.minX, y: rect.maxY))
        p.closeSubpath()
        return p
    }
}

/// The family accent, by vendor (brand/README.md, "Which dash is which"). PS5 and PS4 share the blue.
extension ConsoleFamily {
    var accent: Color { Brand.blue }
}

struct ConsoleTile: View {
    let console: PairedConsole
    let reachability: Reachability
    let isSelected: Bool
    let isFocused: Bool
    let isLive: Bool
    var connect: () -> Void

    @State private var hovering = false
    @Environment(\.colorScheme) private var scheme
    @Environment(\.colorSchemeContrast) private var contrast

    private var displayName: String { console.name.isEmpty ? console.host : console.name }
    /// Not found loses the family accent and nothing else (docs/design.md, "Quiet, not absent").
    private var quiet: Bool { reachability == .notFound }

    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 6) {
                Text(displayName)
                    .font(.title3.weight(.semibold))
                    .lineLimit(2)
                Spacer(minLength: 0)
                HStack(spacing: 6) {
                    Text(console.family.rawValue)
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .overlay(Capsule().strokeBorder(.secondary.opacity(0.6), lineWidth: 1))
                    if isLive {
                        Text("Playing").font(.callout).foregroundStyle(.secondary)
                    } else if let label = reachability.label {
                        Text(label).font(.callout).foregroundStyle(.secondary)
                    }
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)

            Button(action: connect) {
                ZStack {
                    WedgeFacet()
                        .fill(facetFill)
                    PlayGlyph()
                        .fill(scheme == .dark || !quiet ? Color.white : Color.primary)
                        .frame(width: 22, height: 26)
                        .offset(x: 8)
                }
                .frame(width: 92)
                .contentShape(WedgeFacet())
            }
            .buttonStyle(.plain)
            .accessibilityHidden(true)
        }
        .frame(height: 132)
        .background(TileGround())
        .overlay {
            if hovering { Color.primary.opacity(0.05) }   // hover is a wash
        }
        .clipShape(.rect(cornerRadius: 14))
        .overlay {
            RoundedRectangle(cornerRadius: 14)
                .strokeBorder(borderColor, lineWidth: isSelected ? 3 : (contrast == .increased ? 1.5 : 0.5))
        }
        .onHover { hovering = $0 }
        .contentShape(.rect(cornerRadius: 14))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibilityText)
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
        .accessibilityAction(named: "Connect", connect)
    }

    private var facetFill: AnyShapeStyle {
        if contrast == .increased { return AnyShapeStyle(quiet ? Color.secondary : Color.accentColor) }
        if quiet { return AnyShapeStyle(Color.secondary.opacity(0.35)) }
        return AnyShapeStyle(LinearGradient(colors: [console.family.accent, console.family.accent.opacity(0.75)],
                                            startPoint: .top, endPoint: .bottom))
    }

    /// Selection is a ring in the system accent while the grid has focus, and grey when it does not: the
    /// Finder's convention for a selection in an inactive list.
    private var borderColor: Color {
        if isSelected { return isFocused ? .accentColor : .secondary }
        return .primary.opacity(contrast == .increased ? 0.6 : 0.12)
    }

    private var accessibilityText: String {
        var parts = [displayName, console.family.rawValue]
        if isLive { parts.append(String(localized: "Playing")) } else if let l = reachability.label { parts.append(l) }
        return parts.joined(separator: ", ")
    }
}

/// A console on the network that is not paired: quieter, dashed, and its action is Pair, not a wedge,
/// because a wedge means "this launches something" (docs/design.md, "Form").
struct PairTile: View {
    let console: DiscoveredConsole
    var pair: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(console.name).font(.title3.weight(.semibold)).foregroundStyle(.secondary).lineLimit(2)
            Spacer(minLength: 0)
            HStack {
                Text(console.hostType).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                Spacer()
                Button("Pair…", action: pair)
            }
        }
        .padding(16)
        .frame(height: 132)
        .frame(maxWidth: .infinity, alignment: .leading)
        .overlay {
            RoundedRectangle(cornerRadius: 14).strokeBorder(style: StrokeStyle(lineWidth: 1, dash: [5, 4]))
                .foregroundStyle(.secondary.opacity(0.6))
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(console.name), \(console.hostType), not paired")
    }
}

#Preview("Tiles") {
    let console = PairedConsole(host: "192.0.2.104", name: "PS5-8A2F", consoleID: "id", accountID: "1", family: .ps5,
                                registrationKey: [], companion: [])
    VStack(spacing: 16) {
        ConsoleTile(console: console, reachability: .ready, isSelected: true, isFocused: true, isLive: false) {}
        ConsoleTile(console: console, reachability: .resting, isSelected: false, isFocused: false, isLive: false) {}
        ConsoleTile(console: console, reachability: .notFound, isSelected: false, isFocused: false, isLive: false) {}
    }
    .frame(width: 280)
    .padding()
}
