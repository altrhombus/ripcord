// Is anything wrong, and is it me or the network: the HUD ladder's first two rungs (DESIGN.md, "The inspector
// is the ladder"). The thresholds are the dotnet client's, from src/Ripcord.Core/Sessions/
// StreamHealthAssessor.cs, so the two clients call the same stream by the same name. Only the rules whose
// signals the Mac has are ported: the device rules there read decode and present counts the display layer
// does not expose.

import Foundation
import RipcordKit

enum StreamHealth {
    static let lossWarnRatio = 0.02
    static let lossBadRatio = 0.10
    static let rttWarnMs = 60.0
    static let rttBadMs = 120.0
    static let stallMs: UInt32 = 2_000
    static let fpsShortfallFactor = 0.75

    enum Severity: Comparable { case normal, warning, critical }

    struct Verdict: Equatable {
        var severity: Severity
        var title: String
        var advice: String?
    }

    static func severity(loss ratio: Double) -> Severity {
        ratio >= lossBadRatio ? .critical : ratio >= lossWarnRatio ? .warning : .normal
    }

    static func severity(rttMs: Double) -> Severity {
        rttMs >= rttBadMs ? .critical : rttMs >= rttWarnMs ? .warning : .normal
    }

    static func assess(_ sample: MetricSample?, stats: SessionStats?, targetFPS: Int) -> Verdict {
        guard let stats, let sample else { return Verdict(severity: .normal, title: String(localized: "Starting")) }
        // Liveness first: a stalled stream makes every throughput rule read as a healthy zero.
        if stats.videoFrames > 0 && stats.msSinceVideoFrame >= stallMs {
            return Verdict(severity: .critical, title: String(localized: "The stream has stopped"),
                           advice: String(localized: "Video stopped arriving from the console."))
        }
        let loss = severity(loss: sample.lossRatio)
        let rtt = severity(rttMs: sample.rttMs)
        if loss > .normal {
            return Verdict(severity: loss, title: String(localized: "The network is dropping packets"),
                           advice: String(localized: "A wired connection, or being closer to the router, usually helps."))
        }
        if rtt > .normal {
            return Verdict(severity: rtt, title: String(localized: "The network is slow to respond"),
                           advice: String(localized: "Controls may feel late even though the picture is clean."))
        }
        if sample.fps > 0 && sample.fps < Double(targetFPS) * fpsShortfallFactor {
            return Verdict(severity: .warning, title: String(localized: "Fewer frames than asked for"),
                           advice: String(localized: "The console is sending fewer frames than the stream's rate."))
        }
        return Verdict(severity: .normal, title: String(localized: "Everything looks good"))
    }
}

/// One stats interval, as rates: the counters in SessionStats are cumulative.
struct MetricSample: Identifiable, Equatable {
    let id: Int
    var kbps: Double
    var lossRatio: Double
    var rttMs: Double
    var fps: Double

    /// The interval between two cumulative reports. The engine reports about once a second, and the interval
    /// is taken as one second, as the dotnet client's HUD does. Counters are subtracted wrapping, so a restart that
    /// resets them reads as one odd interval rather than a trap.
    static func between(_ previous: SessionStats, _ current: SessionStats, id: Int) -> MetricSample {
        let received = Double(current.packetsReceived &- previous.packetsReceived)
        let lost = Double(current.packetsLost &- previous.packetsLost)
        let frames = Double(current.videoFrames &- previous.videoFrames)
        return MetricSample(id: id, kbps: Double(current.kbps),
                            lossRatio: received + lost > 0 ? lost / (received + lost) : 0,
                            rttMs: current.rttMs, fps: frames)
    }
}

/// Rung 1's hysteresis: a warning appears after two bad intervals in a row and clears after three good ones,
/// so one noisy second neither raises it nor drops it.
struct WarningLatch {
    private(set) var shown: StreamHealth.Verdict?
    private var bad = 0
    private var good = 0

    mutating func feed(_ verdict: StreamHealth.Verdict) {
        if verdict.severity > .normal {
            bad += 1
            good = 0
            if bad >= 2 { shown = verdict }
        } else {
            good += 1
            bad = 0
            if good >= 3 { shown = nil }
        }
    }
}
