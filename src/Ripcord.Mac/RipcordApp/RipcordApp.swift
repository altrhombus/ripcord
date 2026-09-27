// Ripcord for Mac. One library window of paired consoles, and a stream window per session.
//
// The protocol is the Rust engine's, reached through RipcordKit; this target is the Mac app around it:
// windows, audio and video presentation, and the host-side choices (where pairings live, what a person is
// asked). docs/macos-plan.md has the order of work and the design direction; DESIGN.md beside this file
// settles the surfaces.

import SwiftUI
import RipcordKit

@main
struct RipcordApp: App {
    @State private var model = AppModel()

    var body: some Scene {
        Window("Ripcord", id: "library") {
            LibraryView()
                .environment(model)
                .frame(minWidth: 560, minHeight: 360)
        }
        .defaultSize(width: 760, height: 480)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("Pair a Console…") { model.pairing = PairingRequest() }
                    .keyboardShortcut("n")
            }
        }

        WindowGroup("Stream", id: "stream", for: StreamTarget.self) { $target in
            if let target {
                StreamWindow(target: target)
                    .environment(model)
            }
        }
        .defaultSize(width: 1280, height: 720)
        .windowResizability(.contentMinSize)
        .windowStyle(.hiddenTitleBar)
        .commands { StreamCommands() }

        Settings {
            SettingsView()
                .environment(model)
        }
    }
}
