// A stream window. Step 4's version is plain: the picture, a status line until it streams, the passcode
// prompt, and a small readout. The QuickTime-style controls and the inspector come with step 7.

import SwiftUI
import RipcordKit

struct StreamWindow: View {
    let target: StreamTarget
    @Environment(AppModel.self) private var model
    @State private var controller: StreamController?
    @State private var digits = ""
    @State private var showStats = true

    var body: some View {
        ZStack(alignment: .bottomLeading) {
            if let controller {
                VideoLayerView(surface: controller.surface)
                    .ignoresSafeArea()
                if !controller.status.isLive {
                    VStack(spacing: 8) {
                        ProgressView()
                        Text(label(for: controller)).foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
                if showStats, let s = controller.stats {
                    Text(readout(s, controller))
                        .font(.caption.monospacedDigit())
                        .padding(6)
                        .background(.black.opacity(0.5), in: .rect(cornerRadius: 6))
                        .foregroundStyle(.white)
                        .padding(10)
                }
            } else {
                ContentUnavailableView("This console is no longer paired", systemImage: "questionmark.circle")
            }
        }
        .background(.black)
        .navigationTitle(controller?.console.name ?? "Ripcord")
        .onAppear(perform: start)
        .onDisappear { controller?.stop() }
        .sheet(isPresented: .constant(controller?.passcodeRequest != nil)) {
            passcodeSheet
        }
        .onKeyPress(.init("s"), phases: .down) { _ in
            showStats.toggle()
            return .handled
        }
    }

    private var passcodeSheet: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(controller?.passcodeRequest ?? 0 > 0 ? "That passcode was refused" : "The console is locked")
                .font(.headline)
            Text("Enter the passcode of the console's user.").foregroundStyle(.secondary)
            SecureField("Passcode", text: $digits)
                .textContentType(.password)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { controller?.cancelPasscode() }
                Button("Sign In") {
                    controller?.submitPasscode(digits)
                    digits = ""
                }
                .keyboardShortcut(.defaultAction)
                .disabled(digits.isEmpty)
            }
        }
        .padding(20)
        .frame(width: 340)
    }

    private func start() {
        guard controller == nil, let console = model.console(for: target) else { return }
        let c = StreamController(console: console, settings: model.settings)
        controller = c
        c.start()
    }

    private func label(for c: StreamController) -> String {
        switch c.status.lifecycle {
        case .failed: "Could not connect: \(c.status.detail)"
        case .reconnecting: "Reconnecting…"
        case .closed: "Disconnected"
        default: "Connecting (\(String(describing: c.stage)))…"
        }
    }

    private func readout(_ s: SessionStats, _ c: StreamController) -> String {
        var parts = ["\(s.kbps / 1000) Mb/s", "lost \(s.packetsLost)", String(format: "rtt %.1f ms", s.rttMs)]
        if let i = c.info { parts.insert("\(i.width)×\(i.height) \(i.codec)", at: 0) }
        if let l = c.latencyMs { parts.append(String(format: "display %.0f ms", l)) }
        return parts.joined(separator: "  ·  ")
    }
}
