// The widget extension (DESIGN.md, "Present across the Mac"): a desktop widget of the paired consoles, and a
// Control Center control that connects to one. Both read the snapshot the app shares
// (Shared/ConsoleSnapshot.swift) and act through the app's intents (Shared/ConsoleIntents.swift); nothing
// here touches a pairing record or the network.

import AppIntents
import SwiftUI
import WidgetKit

@main
struct RipcordWidgets: WidgetBundle {
    var body: some Widget {
        ConsolesWidget()
        ConnectControl()
    }
}

// MARK: - The widget

struct ConsolesEntry: TimelineEntry {
    let date: Date
    /// Nil when there is no snapshot to read: the build is not entitled to the app group, or the app has
    /// not run since it was installed.
    let snapshot: ConsoleSnapshot?
}

struct ConsolesProvider: TimelineProvider {
    func placeholder(in context: Context) -> ConsolesEntry {
        ConsolesEntry(date: .now, snapshot: ConsoleSnapshot(consoles: [
            .init(id: "preview", name: "PS5", family: "PS5", state: "ready"),
        ], written: .now))
    }

    func getSnapshot(in context: Context, completion: @escaping (ConsolesEntry) -> Void) {
        completion(context.isPreview ? placeholder(in: context) : ConsolesEntry(date: .now, snapshot: ConsoleSnapshot.load()))
    }

    /// The app reloads the timeline whenever what it shares changes, so one entry and no schedule.
    func getTimeline(in context: Context, completion: @escaping (Timeline<ConsolesEntry>) -> Void) {
        completion(Timeline(entries: [ConsolesEntry(date: .now, snapshot: ConsoleSnapshot.load())], policy: .never))
    }
}

struct ConsolesWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "io.github.altrhombus.Ripcord.consoles", provider: ConsolesProvider()) { entry in
            ConsolesWidgetView(entry: entry)
                .containerBackground(for: .widget) {
                    LinearGradient(colors: [Color(red: 0x30 / 255, green: 0x37 / 255, blue: 0x43 / 255),
                                            Color(red: 0x1B / 255, green: 0x1F / 255, blue: 0x26 / 255)],
                                   startPoint: .topLeading, endPoint: .bottomTrailing)
                }
        }
        .configurationDisplayName("Consoles")
        .description("Your paired consoles. Click one to play.")
        .supportedFamilies([.systemSmall, .systemMedium])
    }
}

struct ConsolesWidgetView: View {
    let entry: ConsolesEntry
    @Environment(\.widgetFamily) private var family

    var body: some View {
        if let consoles = entry.snapshot?.consoles, !consoles.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                ForEach(consoles.prefix(family == .systemSmall ? 2 : 4)) { console in
                    Button(intent: ConnectToConsoleIntent(console: ConsoleEntity(id: console.id, name: console.name,
                                                                                  family: console.family))) {
                        HStack {
                            VStack(alignment: .leading, spacing: 1) {
                                Text(console.name).font(.headline).lineLimit(1)
                                Text("\(console.family) · \(Self.label(console.state))")
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer(minLength: 0)
                            Image(systemName: "play.fill").foregroundStyle(.secondary)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .buttonStyle(.plain)
                }
                Spacer(minLength: 0)
            }
            .foregroundStyle(.white)
        } else {
            VStack(alignment: .leading, spacing: 6) {
                Image("MenuBarMark").resizable().frame(width: 28, height: 28)
                Spacer(minLength: 0)
                Text("Open Ripcord to pair a console.").font(.callout)
            }
            .foregroundStyle(.white)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    static func label(_ state: String) -> LocalizedStringResource {
        switch state {
        case "ready": "Ready"
        case "resting": "Resting"
        case "notFound": "Not found"
        case "playing": "Playing"
        default: ""
        }
    }
}

// MARK: - The Control

/// "Connect to *console*", configured per control (DESIGN.md, "Present across the Mac").
struct ConnectControl: ControlWidget {
    var body: some ControlWidgetConfiguration {
        AppIntentControlConfiguration(kind: "io.github.altrhombus.Ripcord.connect",
                                      intent: ConnectControlConfiguration.self) { configuration in
            ControlWidgetButton(action: connectIntent(configuration.console)) {
                Label(configuration.console?.name ?? String(localized: "Choose a Console"), image: "MenuBarMark")
            }
        }
        .displayName("Connect to Console")
        .description("Starts playing from a paired console.")
    }

    private func connectIntent(_ console: ConsoleEntity?) -> ConnectToConsoleIntent {
        guard let console else { return ConnectToConsoleIntent() }
        return ConnectToConsoleIntent(console: console)
    }
}
