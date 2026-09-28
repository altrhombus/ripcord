// The paired consoles, and the way to pair one. Plain lists for now: the tiles, the launch and the rest of the
// Mac's DESIGN.md wait for the mobile design pass (docs/ios-plan.md).

import SwiftUI
import RipcordKit

struct LibraryScreen: View {
    @Environment(MobileModel.self) private var model
    @State private var streaming: PairedConsole?

    var body: some View {
        @Bindable var model = model
        NavigationStack {
            List {
                if model.consoles.isEmpty {
                    Text("Pair a console to play from it. It needs to be on the same network as this device.")
                        .foregroundStyle(.secondary)
                }
                ForEach(model.consoles, id: \.host) { console in
                    Button {
                        streaming = console
                    } label: {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(console.name.isEmpty ? console.host : console.name).font(.headline)
                            Text([console.family.rawValue, model.reachability(of: console).label].compactMap { $0 }
                                .joined(separator: " · "))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .contextMenu {
                        Button("Forget", role: .destructive) { model.forget(console) }
                    }
                }
            }
            .navigationTitle("Ripcord")
            .toolbar {
                ToolbarItem {
                    Button("Pair a Console", systemImage: "plus") { model.pairing = true }
                }
            }
            .sheet(isPresented: $model.pairing) {
                PairingScreen().environment(model)
            }
            .fullScreenCover(item: Binding(get: { streaming.map(StreamItem.init) }, set: { streaming = $0?.console })) { item in
                StreamScreen(console: item.console, settings: model.settings)
            }
        }
    }
}

private struct StreamItem: Identifiable {
    let console: PairedConsole
    var id: String { console.consoleID + console.host }
}
