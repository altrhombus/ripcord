// The library (DESIGN.md, "The library"): one window, a grid of tiles, a toolbar, and nothing else.
// Double-click or Return connects, ⌘N pairs, the arrow keys move the selection, and a controller drives
// the same selection while the window is key.

import SwiftUI
import RipcordKit

struct LibraryView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @Environment(\.controlActiveState) private var activeState
    @FocusState private var gridFocused: Bool
    @State private var columns = 1
    @State private var renaming: PairedConsole?
    @State private var newName = ""
    @State private var forgetting: PairedConsole?
    @State private var details: PairedConsole?
    @State private var navigator: PadNavigator?
    @State private var tileFrames: [String: CGRect] = [:]
    @State private var window: NSWindow?

    private static let tileMinimum: CGFloat = 250
    private static let spacing: CGFloat = 16

    var body: some View {
        @Bindable var model = model
        Group {
            if model.consoles.isEmpty && model.unpairedNearby.isEmpty {
                FirstRunView()
            } else {
                grid
            }
        }
        .navigationTitle("Ripcord")
        .toolbar {
            ToolbarItem {
                Button { model.pairing = PairingRequest() } label: { Label("Pair a Console", systemImage: "plus") }
                    .help("Pair a Console (⌘N)")
            }
        }
        .sheet(item: $model.pairing) { request in
            PairingSheet(request: request).environment(model)
        }
        .alert("Rename Console", isPresented: .constant(renaming != nil), presenting: renaming) { console in
            TextField("Name", text: $newName)
            Button("Rename") { model.rename(console, to: newName); renaming = nil }
            Button("Cancel", role: .cancel) { renaming = nil }
        }
        .confirmationDialog("Forget \(forgetting?.name ?? "")?", isPresented: .constant(forgetting != nil),
                            presenting: forgetting) { console in
            Button("Forget", role: .destructive) { try? model.forget(console); forgetting = nil }
            Button("Cancel", role: .cancel) { forgetting = nil }
        } message: { _ in
            Text("To play from it again, you will need to pair it again.")
        }
        .sheet(item: Binding(get: { details.map(DetailsItem.init) }, set: { details = $0?.console })) { item in
            ConsoleDetails(console: item.console)
        }
        .background(WindowReader { window = $0 })
        .onChange(of: activeState, initial: true) { _, state in
            if state == .key { claimPad() }
        }
    }

    private var grid: some View {
        GeometryReader { geometry in
            ScrollView {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: Self.tileMinimum), spacing: Self.spacing)],
                          spacing: Self.spacing) {
                    ForEach(model.consoles, id: \.host) { console in
                        let key = model.key(of: console)
                        ConsoleTile(console: console, reachability: model.reachability(of: console),
                                    isSelected: model.selection == key, isFocused: gridFocused,
                                    isLive: model.liveStreams[key] != nil) { connect(console) }
                            .onTapGesture(count: 2) { connect(console) }
                            .onTapGesture { model.selection = key; gridFocused = true }
                            .contextMenu { menu(for: console) }
                            .onGeometryChange(for: CGRect.self) { $0.frame(in: .global) } action: { tileFrames[key] = $0 }
                    }
                    ForEach(model.unpairedNearby, id: \.hostID) { found in
                        PairTile(console: found) { model.pairing = PairingRequest(preselected: found) }
                    }
                }
                .padding(20)
            }
            .onChange(of: geometry.size.width, initial: true) { _, width in
                // GridItem.adaptive's own arithmetic, so the pad's up and down land on the tile drawn above.
                let usable = width - 40
                columns = max(1, Int((usable + Self.spacing) / (Self.tileMinimum + Self.spacing)))
            }
        }
        .focusable()
        .focusEffectDisabled()
        .focused($gridFocused)
        .onMoveCommand { direction in
            switch direction {
            case .up: move(.up)
            case .down: move(.down)
            case .left: move(.left)
            case .right: move(.right)
            @unknown default: break
            }
        }
        .onKeyPress(.return) {
            guard let selected = selectedConsole else { return .ignored }
            connect(selected)
            return .handled
        }
        .onAppear {
            gridFocused = true
            if model.selection == nil { model.selection = model.consoles.first.map(model.key(of:)) }
        }
    }

    @ViewBuilder
    private func menu(for console: PairedConsole) -> some View {
        Button("Connect") { connect(console) }
        Button("Wake") { model.wake(console) }
            .disabled(model.reachability(of: console) == .ready)
        Divider()
        Button("Rename…") { newName = console.name; renaming = console }
        Button("Show Details") { details = console }
        Divider()
        Button("Forget…", role: .destructive) { forgetting = console }
    }

    private var selectedConsole: PairedConsole? {
        model.selection.flatMap(model.console(forKey:))
    }

    private func connect(_ console: PairedConsole) {
        let key = model.key(of: console)
        model.selection = key
        if let frame = tileFrames[key], let window, let content = window.contentView {
            // SwiftUI's global space is the window's content, top-left origin; AppKit's is bottom-left.
            let inWindow = NSRect(x: frame.minX, y: content.bounds.height - frame.maxY, width: frame.width, height: frame.height)
            model.launchOrigins[key] = window.convertToScreen(content.convert(inWindow, to: nil))
        }
        openWindow(id: "stream", value: StreamTarget(consoleKey: key))
    }

    private func move(_ direction: PadNavigator.Move) {
        let keys = model.consoles.map(model.key(of:))
        guard !keys.isEmpty else { return }
        guard let current = model.selection.flatMap({ keys.firstIndex(of: $0) }) else {
            model.selection = keys[0]
            return
        }
        let next: Int
        switch direction {
        case .left: next = current - 1
        case .right: next = current + 1
        case .up: next = current - columns
        case .down: next = current + columns
        case .activate:
            if let console = selectedConsole { connect(console) }
            return
        }
        if keys.indices.contains(next) { model.selection = keys[next] }
    }

    /// The library takes the pad whenever its window becomes key; a stream window takes it back the same way.
    private func claimPad() {
        let navigator = self.navigator ?? PadNavigator { [move] in move($0) }
        self.navigator = navigator
        model.input.claim(navigator, sink: navigator.sink)
    }
}

private struct DetailsItem: Identifiable {
    let console: PairedConsole
    var id: String { console.consoleID + console.host }
}

/// Nothing paired (DESIGN.md, "The library"): the mark, one sentence, one primary action, and the
/// same-network limit stated before anyone meets it. Not an error, so not an alert.
struct FirstRunView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(spacing: 16) {
            RipcordMark(wedge: scheme == .dark ? .white : Color(red: 0x17 / 255, green: 0x19 / 255, blue: 0x1D / 255))
                .frame(width: 96, height: 96)
            Text("Play your PlayStation on this Mac.")
                .font(.title2.weight(.semibold))
            Text("Your Mac and your console need to be on the same network.")
                .foregroundStyle(.secondary)
            Button("Pair a Console…") { model.pairing = PairingRequest() }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
            if !PairingFileStore.lab.load().isEmpty {
                Button("Bring In Consoles Paired with ripcord-lab") { model.importLabPairings() }
                    .buttonStyle(.link)
            }
        }
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

struct ConsoleDetails: View {
    let console: PairedConsole
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(console.name).font(.title2.weight(.semibold))
            Form {
                LabeledContent("Family", value: console.family.rawValue)
                LabeledContent("Address", value: console.host)
                LabeledContent("Console ID", value: console.consoleID.isEmpty ? "—" : console.consoleID)
                LabeledContent("Paired for account", value: console.accountID)
            }
            .textSelection(.enabled)
            HStack {
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 400)
    }
}
