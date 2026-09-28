// What the app keeps and what it calls things: settings that survive an upgrade, key bindings, key names,
// and the three connect stages.

import Foundation
import GameController
import RipcordKit
import Testing

@Suite("Settings and labels")
struct SettingsAndLabelsTests {
    @Test("settings saved by an older build keep what they had and take defaults for the rest")
    func partialDecoding() throws {
        // Written before most of today's settings existed, with one whose type has since changed.
        let old = #"{"width":1280,"height":720,"fps":30,"bitrateKbps":8000,"allowHEVC":false,"hdr":"yes"}"#
        let settings = try JSONDecoder().decode(StreamSettings.self, from: Data(old.utf8))
        #expect(settings.height == 720)
        #expect(settings.fps == 30)
        #expect(settings.bitrateKbps == 8_000)
        #expect(settings.allowHEVC == false)
        #expect(settings.hdr == StreamSettings().hdr)           // unreadable: the default, not a failure
        #expect(settings.showMenuBarExtra == false)             // absent: the default
        #expect(settings.keyBindings.isEmpty)
    }

    @Test("settings round-trip, key bindings included")
    func roundTrip() throws {
        var settings = StreamSettings()
        settings.fullScreenOnConnect = true
        settings.keyBindings = [Int(GCKeyCode.keyJ.rawValue): .south]
        let decoded = try JSONDecoder().decode(StreamSettings.self, from: JSONEncoder().encode(settings))
        #expect(decoded == settings)
    }

    @Test("no bindings means the defaults; custom bindings replace them")
    func bindings() {
        var settings = StreamSettings()
        #expect(settings.inputBindings == InputBindings())
        settings.keyBindings = [Int(GCKeyCode.keyJ.rawValue): .south]
        #expect(settings.inputBindings.keyboard == [GCKeyCode.keyJ: .south])
    }

    @Test("keys are named by their HID usage")
    func keyNames() {
        #expect(KeyNames.name(.keyA) == "A")
        #expect(KeyNames.name(.keyZ) == "Z")
        #expect(KeyNames.name(.one) == "1")
        #expect(KeyNames.name(.zero) == "0")
        #expect(KeyNames.name(.spacebar) == "Space")
        #expect(KeyNames.name(.upArrow) == "↑")
        #expect(KeyNames.name(.F1) == "F1")
        #expect(KeyNames.name(GCKeyCode(rawValue: 0x99)) == "Key 0x99")
    }

    @Test("every action has a name")
    func actionTitles() {
        #expect(Set(InputAction.allCases.map(\.title)).count == InputAction.allCases.count)
    }

    @Test("the dashes fill at the three stages DESIGN.md names")
    func connectProgress() {
        #expect(ConnectProgress.dashes(for: .idle) == 0)
        #expect(ConnectProgress.dashes(for: .controlOpen) == 1)
        #expect(ConnectProgress.dashes(for: .signedIn) == 1)
        #expect(ConnectProgress.dashes(for: .sessionReady) == 2)
        #expect(ConnectProgress.dashes(for: .streamKeys) == 2)
        #expect(ConnectProgress.dashes(for: .streamReady) == 3)
        #expect(ConnectProgress.dashes(for: .streaming) == 3)
        #expect(ConnectProgress.dashes(for: .ended) == 0)
    }
}
