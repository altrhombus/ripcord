// What a stream asks the console for, and how the app behaves around one: the settings the app keeps.

import Foundation
import GameController
import RipcordKit

/// What a stream asks the console for, and how the app behaves around one, kept across launches. Decoded
/// field by field with defaults, so a setting added later does not reset the ones a person already chose.
struct StreamSettings: Codable, Equatable {
    var width = 1920
    var height = 1080
    var fps = 60
    /// 25 Mb/s: the console grants resolution by bitrate (journal, 2026-09-25).
    var bitrateKbps = 25_000
    var allowHEVC = true
    var hdr = false
    var reportConnectionQuality = false
    var restConsoleOnDisconnect = false
    var fullScreenOnConnect = false
    var askBeforeDisconnecting = false
    var showMenuBarExtra = false
    /// Keyboard bindings, by HID usage (GCKeyCode's raw value) to action. Empty means the defaults.
    var keyBindings: [Int: InputAction] = [:]

    static let key = "streamSettings"

    init() {}

    init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = StreamSettings()
        width = (try? c.decode(Int.self, forKey: .width)) ?? d.width
        height = (try? c.decode(Int.self, forKey: .height)) ?? d.height
        fps = (try? c.decode(Int.self, forKey: .fps)) ?? d.fps
        bitrateKbps = (try? c.decode(Int.self, forKey: .bitrateKbps)) ?? d.bitrateKbps
        allowHEVC = (try? c.decode(Bool.self, forKey: .allowHEVC)) ?? d.allowHEVC
        hdr = (try? c.decode(Bool.self, forKey: .hdr)) ?? d.hdr
        reportConnectionQuality = (try? c.decode(Bool.self, forKey: .reportConnectionQuality)) ?? d.reportConnectionQuality
        restConsoleOnDisconnect = (try? c.decode(Bool.self, forKey: .restConsoleOnDisconnect)) ?? d.restConsoleOnDisconnect
        fullScreenOnConnect = (try? c.decode(Bool.self, forKey: .fullScreenOnConnect)) ?? d.fullScreenOnConnect
        askBeforeDisconnecting = (try? c.decode(Bool.self, forKey: .askBeforeDisconnecting)) ?? d.askBeforeDisconnecting
        showMenuBarExtra = (try? c.decode(Bool.self, forKey: .showMenuBarExtra)) ?? d.showMenuBarExtra
        keyBindings = (try? c.decode([Int: InputAction].self, forKey: .keyBindings)) ?? d.keyBindings
    }

    static func load() -> StreamSettings {
        guard let data = UserDefaults.standard.data(forKey: key),
              let s = try? JSONDecoder().decode(StreamSettings.self, from: data) else { return StreamSettings() }
        return s
    }

    func save() {
        if let data = try? JSONEncoder().encode(self) { UserDefaults.standard.set(data, forKey: Self.key) }
    }

    var inputBindings: InputBindings {
        guard !keyBindings.isEmpty else { return InputBindings() }
        return InputBindings(keyboard: Dictionary(uniqueKeysWithValues: keyBindings.map { (GCKeyCode(rawValue: $0.key), $0.value) }))
    }
}
