// Spotlight and Shortcuts: the intents in Shared/ConsoleIntents.swift, offered without setup.

import AppIntents

struct RipcordShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(intent: ConnectToConsoleIntent(), phrases: [
            "Connect to \(\.$console) with \(.applicationName)",
            "Play \(\.$console) with \(.applicationName)",
        ], shortTitle: "Connect", systemImageName: "play.fill")
        AppShortcut(intent: WakeConsoleIntent(), phrases: [
            "Wake \(\.$console) with \(.applicationName)",
        ], shortTitle: "Wake", systemImageName: "power")
    }
}
