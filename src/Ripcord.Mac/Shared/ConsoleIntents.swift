// App Intents: Connect to Console, Wake Console and Disconnect, over a console entity (DESIGN.md, "Present
// across the Mac"). Shortcuts, Spotlight, the widget and the Control all reach the app through these.
//
// Compiled into the app and the widget extension. The app's copy does the work: every intent here opens or
// runs in the app (RIPCORD_APP is the app target's compilation condition), because the pairing records and
// the network live there. The extension's copy exists so a widget button and a Control can name the intent;
// that the system then runs it in the app, as openAppWhenRun and the intent's presence in both asks, is
// [X] until a signed build has been seen to do it.

import AppIntents
import Foundation

struct ConsoleEntity: AppEntity, Identifiable {
    static let typeDisplayRepresentation: TypeDisplayRepresentation = "Console"
    static let defaultQuery = ConsoleQuery()

    let id: String
    let name: String
    let family: String

    var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(title: "\(name)", subtitle: "\(family)")
    }
}

struct ConsoleQuery: EntityQuery {
    func entities(for identifiers: [ConsoleEntity.ID]) async throws -> [ConsoleEntity] {
        await all().filter { identifiers.contains($0.id) }
    }

    func suggestedEntities() async throws -> [ConsoleEntity] {
        await all()
    }

    /// The app reads its own records; the extension reads the snapshot the app shares.
    private func all() async -> [ConsoleEntity] {
        #if RIPCORD_APP
        await MainActor.run { AppModel.shared.consoleEntities }
        #else
        (ConsoleSnapshot.load()?.consoles ?? []).map { ConsoleEntity(id: $0.id, name: $0.name, family: $0.family) }
        #endif
    }
}

struct ConnectToConsoleIntent: AppIntent {
    static let title: LocalizedStringResource = "Connect to Console"
    static let description = IntentDescription("Opens a stream from a paired console, waking it first if it is resting.")
    static let openAppWhenRun = true

    @Parameter(title: "Console")
    var console: ConsoleEntity

    init() {}

    init(console: ConsoleEntity) {
        self.console = console
    }

    static var parameterSummary: some ParameterSummary {
        Summary("Connect to \(\.$console)")
    }

    @MainActor
    func perform() async throws -> some IntentResult {
        #if RIPCORD_APP
        AppModel.shared.openStream(console.id)
        #endif
        return .result()
    }
}

struct WakeConsoleIntent: AppIntent {
    static let title: LocalizedStringResource = "Wake Console"
    static let description = IntentDescription("Wakes a paired console from rest mode, without connecting to it.")

    @Parameter(title: "Console")
    var console: ConsoleEntity

    static var parameterSummary: some ParameterSummary {
        Summary("Wake \(\.$console)")
    }

    @MainActor
    func perform() async throws -> some IntentResult {
        #if RIPCORD_APP
        AppModel.shared.wake(key: console.id)
        #endif
        return .result()
    }
}

struct DisconnectIntent: AppIntent {
    static let title: LocalizedStringResource = "Disconnect from Console"
    static let description = IntentDescription("Ends the stream from a console, if one is open.")

    @Parameter(title: "Console")
    var console: ConsoleEntity

    static var parameterSummary: some ParameterSummary {
        Summary("Disconnect from \(\.$console)")
    }

    @MainActor
    func perform() async throws -> some IntentResult {
        #if RIPCORD_APP
        AppModel.shared.disconnect(key: console.id)
        #endif
        return .result()
    }
}

/// What a Control is configured with: which console it connects to.
struct ConnectControlConfiguration: ControlConfigurationIntent {
    static let title: LocalizedStringResource = "Connect to Console"

    @Parameter(title: "Console")
    var console: ConsoleEntity?
}
