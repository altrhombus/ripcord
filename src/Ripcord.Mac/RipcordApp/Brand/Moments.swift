// The three moments DESIGN.md gives identity to, as views: the connect overlay (the launch), the pairing
// celebration, and the stream capsule. Each has a preview, which is where step 5 prototyped them.

import SwiftUI
import RipcordKit

/// The three dashes, as connect progress (DESIGN.md, "The launch": three real stages).
enum ConnectProgress {
    static func dashes(for stage: SessionStage) -> Int {
        switch stage {
        case .streamReady, .streaming: 3
        case .sessionReady, .senkushaUp, .takionUp, .streamKeys: 2
        case .controlOpen, .signedIn: 1
        case .idle, .ended: 0
        }
    }
}

/// Low and left, where the picture will not be: the mark and one line (DESIGN.md, "The launch").
struct ConnectOverlay: View {
    var dashes: Int
    var line: String
    var detail: String?
    var dimmed = false
    var actions: AnyView?

    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .center, spacing: 14) {
                RipcordMark(filled: dashes, dimmed: dimmed)
                    .frame(width: 44, height: 44)
                    .animation(reduceMotion ? nil : .easeOut(duration: 0.25), value: dashes)
                VStack(alignment: .leading, spacing: 2) {
                    Text(line).font(.title3.weight(.medium)).foregroundStyle(.white)
                    if let detail {
                        Text(detail).font(.callout).foregroundStyle(.white.opacity(0.7))
                    }
                }
            }
            if let actions { actions.padding(.leading, 58) }
        }
        .padding(28)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
        .accessibilityElement(children: .combine)
    }
}

/// "Paired." (DESIGN.md, "Pairing"): the mark assembles, wedge first and then the trail.
struct PairedCelebration: View {
    var consoleName: String
    var playNow: () -> Void
    var done: () -> Void

    @State private var assembled = 0
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(spacing: 18) {
            RipcordMark(filled: assembled, wedge: scheme == .dark ? .white : Color(red: 0x17 / 255, green: 0x19 / 255, blue: 0x1D / 255))
                .frame(width: 120, height: 120)
            VStack(spacing: 4) {
                Text("Paired.").font(.largeTitle.weight(.semibold))
                Text("\(consoleName) is ready to play from this Mac.").foregroundStyle(.secondary)
            }
            HStack {
                Button("Done", action: done)
                Button("Play Now", action: playNow)
            }
            .controlSize(.large)
        }
        .padding(36)
        .task {
            if reduceMotion { assembled = 3; return }
            for n in 1...3 {
                try? await Task.sleep(for: .milliseconds(n == 1 ? 350 : 180))
                withAnimation(.spring(duration: 0.3)) { assembled = n }
            }
        }
    }
}

/// The glass capsule (DESIGN.md, "The stream window"). The system's glass, never a hand-built blur.
struct StreamCapsule: View {
    var consoleName: String
    var recording: Bool
    var canPictureInPicture: Bool
    var onPictureInPicture: () -> Void
    var onRecord: () -> Void
    var onInspector: () -> Void
    var onDisconnect: () -> Void

    var body: some View {
        HStack(spacing: 18) {
            Text(consoleName).font(.headline).lineLimit(1)
            Divider().frame(height: 18)
            Button(action: onPictureInPicture) { Image(systemName: "pip.enter") }
                .help("Picture in Picture")
                .disabled(!canPictureInPicture)
                .accessibilityLabel("Picture in Picture")
            Button(action: onRecord) {
                Image(systemName: recording ? "record.circle.fill" : "record.circle")
                    .foregroundStyle(recording ? Color.red : Color.primary)
            }
            .help(recording ? "Stop Recording" : "Record")
            .accessibilityLabel(recording ? "Stop recording" : "Record")
            Button(action: onInspector) { Image(systemName: "info.circle") }
                .help("Show Inspector (⌘I)")
                .accessibilityLabel("Inspector")
            Button(action: onDisconnect) { Image(systemName: "xmark.circle") }
                .help("Disconnect")
                .accessibilityLabel("Disconnect")
        }
        .buttonStyle(.plain)
        .font(.title3)
        .padding(.horizontal, 20)
        .padding(.vertical, 12)
        .glassEffect(.regular, in: .capsule)
    }
}

#Preview("Launch") {
    TimelineView(.periodic(from: .now, by: 1.2)) { context in
        let step = Int(context.date.timeIntervalSinceReferenceDate / 1.2) % 5
        ConnectOverlay(dashes: min(step, 3), line: step < 4 ? "Connecting to Living Room…" : "Streaming")
    }
    .frame(width: 800, height: 450)
    .background(.black)
}

#Preview("Failed") {
    ConnectOverlay(dashes: 1, line: "PS5-8A2F went to rest", detail: "Connecting will wake it.", dimmed: true,
                   actions: AnyView(HStack { Button("Try Again") {}; Button("Close") {} }))
        .frame(width: 800, height: 450)
        .background(.black)
}

#Preview("Paired") {
    PairedCelebration(consoleName: "PS5-8A2F", playNow: {}, done: {})
}

#Preview("Stream capsule") {
    StreamCapsule(consoleName: "PS5-8A2F", recording: true, canPictureInPicture: true, onPictureInPicture: {},
                  onRecord: {}, onInspector: {}, onDisconnect: {})
        .padding(60)
        .background(LinearGradient(colors: [.indigo, .black], startPoint: .top, endPoint: .bottom))
}
