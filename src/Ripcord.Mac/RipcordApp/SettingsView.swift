// Settings: what a stream asks the console for.

import SwiftUI

struct SettingsView: View {
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
                Text("30").tag(30)
                Text("60").tag(60)
            }
            Stepper("Bitrate: \(model.settings.bitrateKbps / 1000) Mb/s", value: $model.settings.bitrateKbps,
                    in: 2_000...40_000, step: 1_000)
            Toggle("Prefer HEVC", isOn: $model.settings.allowHEVC)
            Toggle("Ask the console to rest when I disconnect", isOn: $model.settings.restConsoleOnDisconnect)
        }
        .formStyle(.grouped)
        .frame(width: 440)
        .padding()
    }
}
