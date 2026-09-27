// What the app shares with its widgets and Controls: the paired consoles' names, families and last-known
// states, in a file in the app group's container (DESIGN.md, "Present across the Mac"). Never a key: the
// pairing records stay in the Keychain, which the extension cannot read and does not need.
//
// Compiled into the app and the widget extension both. When the build is not entitled to the group (an
// ad hoc build has no team to validate one against), there is no container: the app writes nothing, and a
// widget shows the way into the app instead of consoles. [X] until a signed build has been seen to share it.

import Foundation

struct ConsoleSnapshot: Codable, Equatable, Sendable {
    struct Console: Codable, Equatable, Sendable, Identifiable {
        /// The app's console key: the console's id, or its address when it has none.
        var id: String
        var name: String
        var family: String
        /// "ready", "resting", "notFound", "playing", or "unknown".
        var state: String
    }

    var consoles: [Console]
    var written: Date

    static let appGroup = "group.io.github.altrhombus.Ripcord"

    static var url: URL? {
        FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroup)?
            .appending(path: "consoles.json")
    }

    static func load() -> ConsoleSnapshot? {
        guard let url, let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(ConsoleSnapshot.self, from: data)
    }

    func save() {
        guard let url = Self.url, let data = try? JSONEncoder().encode(self) else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: url, options: .atomic)
    }
}
