// The library: the consoles this Mac is paired with. Step 4's version is deliberately plain; the tiles,
// discovery and pairing come with step 6.

import SwiftUI
import RipcordKit

struct LibraryView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Group {
            if model.consoles.isEmpty {
                ContentUnavailableView {
                    Label("No paired consoles", systemImage: "gamecontroller")
                } description: {
                    Text("Pair a console to stream from it.")
                } actions: {
                    Button("Import from ripcord-lab") { model.importLabPairings() }
                }
            } else {
                List(model.consoles, id: \.host) { console in
                    HStack {
                        VStack(alignment: .leading) {
                            Text(console.name.isEmpty ? console.host : console.name).font(.headline)
                            Text("\(console.family.rawValue) · \(console.host)").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Connect") {
                            openWindow(id: "stream", value: StreamTarget(consoleKey: model.key(of: console)))
                        }
                    }
                    .padding(.vertical, 4)
                }
            }
        }
        .navigationTitle("Ripcord")
    }
}
