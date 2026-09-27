// The menu bar extra's menu (DESIGN.md, "Present across the Mac"): each paired console with its state, and
// Connect. A console being streamed says how far its connect has got, in the three stages the stream
// window's mark shows. The label itself stays the one-colour mark: drawing that progress into it waits on
// the custom SF Symbol, whose template format is [X].

import SwiftUI
import RipcordKit

struct MenuBarContent: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        if model.consoles.isEmpty {
            Text("No paired consoles")
        }
        ForEach(model.consoles, id: \.host) { console in
            let key = model.key(of: console)
            Button {
                model.openWindow = model.openWindow ?? openWindow
                model.openStream(key)
            } label: {
                Text(console.name.isEmpty ? console.host : console.name)
                Text(state(of: console, key: key))
            }
        }
        Divider()
        Button("Show Library") {
            NSApp.activate()
            openWindow(id: "library")
        }
        Button("Settings…") {
            NSApp.activate()
            openSettings()
        }
        .keyboardShortcut(",")
        Divider()
        Button("Quit Ripcord") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }

    private func state(of console: PairedConsole, key: String) -> String {
        if let stage = model.liveStreams[key] {
            let dashes = ConnectProgress.dashes(for: stage)
            return dashes >= 3 ? String(localized: "Playing") : String(localized: "Connecting, \(dashes) of 3")
        }
        return model.reachability(of: console).label ?? ""
    }
}
