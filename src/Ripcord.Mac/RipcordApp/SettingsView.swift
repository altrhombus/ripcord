// Settings (DESIGN.md, "Settings"): a stock Mac Settings window, panes in the order the Windows client
// settled. No wedge, no gradient, no Ripcord colour. Each row says what it does for the player.

import SwiftUI
import GameController
import RipcordKit

struct SettingsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        TabView {
            Tab("Picture", systemImage: "tv") { PicturePane() }
            Tab("Controls", systemImage: "gamecontroller") { ControlsPane() }
            Tab("Playing", systemImage: "play.rectangle") { PlayingPane() }
            if model.account.isAvailable {
                // Removed, not disabled, in the edition without the OAuth credential.
                Tab("Account", systemImage: "person.crop.circle") { AccountPane() }
            }
            Tab("Advanced", systemImage: "gearshape.2") { AdvancedPane() }
        }
        .scenePadding()
        .frame(width: 520)
    }
}

private struct PicturePane: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Picker("Resolution", selection: $model.settings.height) {
                Text("720p").tag(720)
                Text("1080p").tag(1080)
            }
            .onChange(of: model.settings.height) { _, h in model.settings.width = h == 720 ? 1280 : 1920 }
            Picker("Frame rate", selection: $model.settings.fps) {
                Text("30 fps").tag(30)
                Text("60 fps").tag(60)
            }
            LabeledContent("Maximum bitrate") {
                Stepper("\(model.settings.bitrateKbps / 1000) Mb/s", value: $model.settings.bitrateKbps,
                        in: 2_000...40_000, step: 1_000)
            }
            Text("The console chooses the resolution partly by bitrate: 1080p needs about 25 Mb/s.")
                .font(.caption).foregroundStyle(.secondary)
            // Codec sits beside HDR because HDR needs HEVC (docs/design.md, "Settings").
            Picker("Video codec", selection: $model.settings.allowHEVC) {
                Text("HEVC when available").tag(true)
                Text("H.264 only").tag(false)
            }
            Toggle("HDR", isOn: $model.settings.hdr)
                .disabled(!model.settings.allowHEVC || !Self.displaySupportsHDR)
            Text(hdrNote).font(.caption).foregroundStyle(.secondary)
        }
        .formStyle(.grouped)
    }

    private static var displaySupportsHDR: Bool {
        NSScreen.screens.contains { $0.maximumPotentialExtendedDynamicRangeColorComponentValue > 1 }
    }

    private var hdrNote: String {
        if !model.settings.allowHEVC { return String(localized: "HDR needs HEVC.") }
        if !Self.displaySupportsHDR { return String(localized: "None of this Mac’s displays shows HDR.") }
        return String(localized: "Used when the game and the console send HDR.")
    }
}

private struct ControlsPane: View {
    @Environment(AppModel.self) private var model
    @State private var recording: InputAction?

    var body: some View {
        Form {
            Section {
                LabeledContent("Release the keyboard", value: "⌃⌥")
                Text("Click the picture to play with the keyboard. Press Control and Option together to give it back to the Mac.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Keyboard") {
                ForEach(InputAction.allCases, id: \.self) { action in
                    LabeledContent(action.title) {
                        Button(recording == action ? String(localized: "Press a key…") : keyName(for: action)) {
                            record(action)
                        }
                        .monospaced()
                    }
                }
                HStack {
                    Spacer()
                    Button("Restore Defaults") { model.settings.keyBindings = [:] }
                        .disabled(model.settings.keyBindings.isEmpty)
                }
            }
        }
        .formStyle(.grouped)
        .frame(height: 520)
        .onDisappear { model.input.hub.cancelKeyCapture() }
    }

    private func keyName(for action: InputAction) -> String {
        let bound = model.settings.inputBindings.keyboard.filter { $0.value == action }.map(\.key)
        guard !bound.isEmpty else { return "—" }
        return bound.map(KeyNames.name).sorted().joined(separator: ", ")
    }

    /// The next key pressed binds to `action`, through the app's one input hub so a stream keeps its keyboard.
    private func record(_ action: InputAction) {
        recording = action
        let model = self.model
        model.input.hub.captureNextKey { key in
            Task { @MainActor in
                defer { recording = nil }
                guard !InputBindings.reservedKeys.contains(key) else { return }
                var map = model.settings.inputBindings.keyboard
                map = map.filter { $0.value != action && $0.key != key }
                map[key] = action
                model.settings.keyBindings = Dictionary(uniqueKeysWithValues: map.map { ($0.key.rawValue, $0.value) })
            }
        }
    }
}

private struct PlayingPane: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Toggle("Go full screen when a stream starts", isOn: $model.settings.fullScreenOnConnect)
            Toggle("Ask before disconnecting", isOn: $model.settings.askBeforeDisconnecting)
            Toggle("Put the console in rest mode when I disconnect", isOn: $model.settings.restConsoleOnDisconnect)
            Section {
                Toggle("Report connection quality to the console", isOn: $model.settings.reportConnectionQuality)
            } footer: {
                Text("Lets the console lower its bitrate when the network struggles. Experimental: off unless you are testing it.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

private struct AccountPane: View {
    @Environment(AppModel.self) private var model
    @State private var window: NSWindow?

    var body: some View {
        Form {
            if let account = model.account.account {
                LabeledContent("Signed in as", value: account.onlineID)
                Section("Consoles on this account") {
                    if model.account.cloudConsoles.isEmpty {
                        Text("None with Remote Play turned on.").foregroundStyle(.secondary)
                    }
                    ForEach(model.account.cloudConsoles, id: \.duid) { console in
                        LabeledContent(console.device.name, value: console.platform)
                    }
                }
                HStack {
                    Spacer()
                    Button("Sign Out") { Task { await model.account.signOut() } }
                }
            } else {
                Text("Signing in lets consoles on your account pair without a code.")
                    .foregroundStyle(.secondary)
                HStack {
                    Spacer()
                    Button("Sign In to PlayStation Network…") { Task { await model.account.signIn(from: window) } }
                        .disabled(model.account.busy)
                }
                if let error = model.account.lastError {
                    Text(error).font(.caption).foregroundStyle(.secondary)
                }
            }
            if !PairingFileStore.lab.load().isEmpty {
                Section("ripcord-lab") {
                    HStack {
                        Text("Bring in consoles paired from the command line.")
                        Spacer()
                        Button("Bring In") { model.importLabPairings() }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .background(WindowReader { window = $0 })
        .task { await model.account.refreshConsoles() }
    }
}

private struct AdvancedPane: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Toggle("Show Ripcord in the menu bar", isOn: $model.settings.showMenuBarExtra)
            LabeledContent("Recordings are saved to", value: Recorder.folder.path(percentEncoded: false))
        }
        .formStyle(.grouped)
    }
}

extension InputAction {
    var title: String {
        switch self {
        case .south: String(localized: "Cross")
        case .east: String(localized: "Circle")
        case .west: String(localized: "Square")
        case .north: String(localized: "Triangle")
        case .dpadUp: String(localized: "D-pad up")
        case .dpadDown: String(localized: "D-pad down")
        case .dpadLeft: String(localized: "D-pad left")
        case .dpadRight: String(localized: "D-pad right")
        case .leftShoulder: String(localized: "L1")
        case .rightShoulder: String(localized: "R1")
        case .leftTrigger: String(localized: "L2")
        case .rightTrigger: String(localized: "R2")
        case .leftStickClick: String(localized: "L3")
        case .rightStickClick: String(localized: "R3")
        case .start: String(localized: "Options")
        case .select: String(localized: "Create")
        case .guide: String(localized: "PS button")
        case .touchpadClick: String(localized: "Touchpad")
        case .leftStickUp: String(localized: "Left stick up")
        case .leftStickDown: String(localized: "Left stick down")
        case .leftStickLeft: String(localized: "Left stick left")
        case .leftStickRight: String(localized: "Left stick right")
        case .rightStickUp: String(localized: "Right stick up")
        case .rightStickDown: String(localized: "Right stick down")
        case .rightStickLeft: String(localized: "Right stick left")
        case .rightStickRight: String(localized: "Right stick right")
        }
    }
}

/// Key names by HID usage (the USB HID Usage Tables, keyboard page 0x07), which is what GCKeyCode carries.
enum KeyNames {
    static func name(_ key: GCKeyCode) -> String {
        let usage = key.rawValue
        switch usage {
        case 0x04...0x1D: return String(UnicodeScalar(UInt8(0x41 + usage - 0x04)))
        case 0x1E...0x26: return String(usage - 0x1D)
        case 0x27: return "0"
        case 0x28: return "Return"
        case 0x29: return "Esc"
        case 0x2A: return "Delete"
        case 0x2B: return "Tab"
        case 0x2C: return "Space"
        case 0x2D: return "-"
        case 0x2E: return "="
        case 0x2F: return "["
        case 0x30: return "]"
        case 0x31: return "\\"
        case 0x33: return ";"
        case 0x34: return "'"
        case 0x35: return "`"
        case 0x36: return ","
        case 0x37: return "."
        case 0x38: return "/"
        case 0x3A...0x45: return "F\(usage - 0x39)"
        case 0x4F: return "→"
        case 0x50: return "←"
        case 0x51: return "↓"
        case 0x52: return "↑"
        case 0xE0: return "Left ⌃"
        case 0xE1: return "Left ⇧"
        case 0xE2: return "Left ⌥"
        case 0xE3: return "Left ⌘"
        case 0xE4: return "Right ⌃"
        case 0xE5: return "Right ⇧"
        case 0xE6: return "Right ⌥"
        case 0xE7: return "Right ⌘"
        default: return String(format: "Key 0x%02X", usage)
        }
    }
}
