// The inspector (⌘I): rungs 2 and 3 of the HUD ladder (DESIGN.md, "The inspector is the ladder"). The top
// answers "is it me or the network?" with a verdict, four numbers and the stream's pills; the disclosure
// groups below hold every field the engine reports.
//
// The colour grammar for instruments (docs/design.md): neutral inside the threshold and semantic once
// crossed; bitrate never colours; the threshold is drawn; scales are fixed; numbers never animate.

import Charts
import SwiftUI
import RipcordKit

struct InspectorView: View {
    let controller: StreamController

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                verdict
                numbers
                pills
                Divider()
                details
            }
            .padding(16)
        }
        .transaction { $0.animation = nil }
    }

    private var verdict: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(controller.status.isLive ? controller.verdict.title : lifecycleTitle)
                .font(.headline)
                .foregroundStyle(tint(controller.status.isLive ? controller.verdict.severity : .normal))
            if controller.status.isLive, let advice = controller.verdict.advice {
                Text(advice).font(.callout).foregroundStyle(.secondary)
            }
        }
    }

    private var lifecycleTitle: String {
        switch controller.status.lifecycle {
        case .reconnecting: String(localized: "Reconnecting")
        case .failed: String(localized: "Not connected")
        case .closed: String(localized: "Disconnected")
        default: String(localized: "Connecting")
        }
    }

    private var latest: MetricSample? { controller.history.last }

    private var numbers: some View {
        Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 10) {
            GridRow {
                figure(String(localized: "Bitrate"), latest.map { String(format: "%.1f Mb/s", $0.kbps / 1000) }, .normal)
                figure(String(localized: "Loss"), latest.map { String(format: "%.1f%%", $0.lossRatio * 100) },
                       latest.map { StreamHealth.severity(loss: $0.lossRatio) } ?? .normal)
            }
            GridRow {
                figure(String(localized: "Round trip"), latest.map { String(format: "%.0f ms", $0.rttMs) },
                       latest.map { StreamHealth.severity(rttMs: $0.rttMs) } ?? .normal)
                figure(String(localized: "Frame rate"), latest.map { String(format: "%.0f fps", $0.fps) }, .normal)
            }
        }
    }

    private func figure(_ label: String, _ value: String?, _ severity: StreamHealth.Severity) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            Text(value ?? "—").font(.title3.monospacedDigit()).foregroundStyle(tint(severity))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }

    private var pills: some View {
        HStack(spacing: 6) {
            if let info = controller.info {
                pill("\(info.width)×\(info.height)")
                pill(info.codec == .hevc ? "HEVC" : "H.264")
            }
            if controller.settings.hdr { pill("HDR") }
            if controller.recording != nil { pill(String(localized: "Recording")) }
        }
    }

    private func pill(_ text: String) -> some View {
        Text(text).font(.caption.weight(.medium))
            .padding(.horizontal, 8).padding(.vertical, 3)
            .background(.quaternary, in: .capsule)
    }

    private var details: some View {
        VStack(alignment: .leading, spacing: 12) {
            DisclosureGroup("Network") {
                chart(String(localized: "Bitrate"), \.kbps, scale: Double(controller.settings.bitrateKbps) * 1.2, threshold: nil)
                // Loss's full scale is the warn threshold, so touching the top means trouble (docs/design.md).
                chart(String(localized: "Loss"), \.lossRatio, scale: StreamHealth.lossWarnRatio, threshold: StreamHealth.lossWarnRatio)
                chart(String(localized: "Round trip"), \.rttMs, scale: StreamHealth.rttWarnMs, threshold: StreamHealth.rttWarnMs)
                if let s = controller.stats {
                    row("Packets received", "\(s.packetsReceived)")
                    row("Packets lost", "\(s.packetsLost)")
                    row("Adaptive target", "\(s.targetBitrateKbps / 1000) Mb/s at \(s.targetHeight)p")
                    row("Since the console was heard", "\(s.msSinceConsoleActivity) ms")
                }
            }
            DisclosureGroup("Video") {
                if let s = controller.stats {
                    row("Frames", "\(s.videoFrames)")
                    row("Keyframes", "\(s.keyframes)")
                    row("Keyframe requests", "\(s.idrRequests)")
                    row("Since the last frame", "\(s.msSinceVideoFrame) ms")
                }
                row("Engine to display", controller.latencyMs.map { String(format: "%.0f ms", $0) } ?? "—")
            }
            DisclosureGroup("Audio") {
                if let s = controller.stats { row("Frames", "\(s.audioFrames)") }
            }
            DisclosureGroup("Session") {
                row("Console", controller.displayName)
                row("Address", controller.console.host)
                row("Stage", String(describing: controller.stage))
                row("Route", String(localized: "This network"))
                if controller.status.reconnectAttempt > 0 { row("Reconnect attempt", "\(controller.status.reconnectAttempt)") }
            }
        }
    }

    private func row(_ label: LocalizedStringKey, _ value: String) -> some View {
        LabeledContent(label) { Text(value).monospacedDigit() }
            .font(.callout)
    }

    private func chart(_ title: String, _ key: KeyPath<MetricSample, Double>, scale: Double, threshold: Double?) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            Chart {
                ForEach(controller.history) { sample in
                    LineMark(x: .value("Second", sample.id), y: .value(title, min(sample[keyPath: key], scale)))
                        .foregroundStyle(threshold.map { sample[keyPath: key] >= $0 ? Color.orange : Color.secondary } ?? .secondary)
                }
                if let threshold {
                    RuleMark(y: .value("Threshold", threshold))
                        .lineStyle(StrokeStyle(lineWidth: 1, dash: [3, 3]))
                        .foregroundStyle(.orange.opacity(0.6))
                }
            }
            .chartYScale(domain: 0...scale)
            .chartXAxis(.hidden)
            .chartYAxis(.hidden)
            .frame(height: 36)
            .accessibilityHidden(true)
        }
        .padding(.vertical, 2)
    }

    private func tint(_ severity: StreamHealth.Severity) -> Color {
        switch severity {
        case .normal: .primary
        case .warning: .orange
        case .critical: .red
        }
    }
}
