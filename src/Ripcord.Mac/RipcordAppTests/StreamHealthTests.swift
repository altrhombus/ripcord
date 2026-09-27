// The HUD ladder's logic: the Windows thresholds, the verdict's order, rung 1's hysteresis, and the rates
// taken from the engine's cumulative counters.

import Foundation
import RipcordKit
import Testing

@Suite("Stream health")
struct StreamHealthTests {
    static func sample(kbps: Double = 20_000, loss: Double = 0, rtt: Double = 10, fps: Double = 60) -> MetricSample {
        MetricSample(id: 1, kbps: kbps, lossRatio: loss, rttMs: rtt, fps: fps)
    }

    @Test("the thresholds are the dotnet client's, and each is inclusive")
    func thresholds() {
        #expect(StreamHealth.severity(loss: 0.019) == .normal)
        #expect(StreamHealth.severity(loss: 0.02) == .warning)
        #expect(StreamHealth.severity(loss: 0.10) == .critical)
        #expect(StreamHealth.severity(rttMs: 59) == .normal)
        #expect(StreamHealth.severity(rttMs: 60) == .warning)
        #expect(StreamHealth.severity(rttMs: 120) == .critical)
    }

    @Test("a stalled stream is reported first, whatever the other numbers say")
    func stallFirst() {
        let stats = SessionStats(videoFrames: 100, msSinceVideoFrame: 2_500)
        let verdict = StreamHealth.assess(Self.sample(loss: 0.5), stats: stats, targetFPS: 60)
        #expect(verdict.severity == .critical)
        #expect(verdict.title.contains("stopped"))
    }

    @Test("a stream that has not yet had a frame is not stalled")
    func notYetStarted() {
        let stats = SessionStats(videoFrames: 0, msSinceVideoFrame: 10_000)
        #expect(StreamHealth.assess(Self.sample(), stats: stats, targetFPS: 60).severity == .normal)
    }

    @Test("loss outranks a slow round trip, which outranks a frame shortfall")
    func order() {
        let stats = SessionStats(videoFrames: 100)
        #expect(StreamHealth.assess(Self.sample(loss: 0.05, rtt: 200, fps: 10), stats: stats, targetFPS: 60).title.contains("dropping"))
        #expect(StreamHealth.assess(Self.sample(rtt: 70, fps: 10), stats: stats, targetFPS: 60).title.contains("slow"))
        #expect(StreamHealth.assess(Self.sample(fps: 40), stats: stats, targetFPS: 60).title.contains("Fewer frames"))
        #expect(StreamHealth.assess(Self.sample(fps: 46), stats: stats, targetFPS: 60).severity == .normal)
    }

    @Test("rung 1 appears after two bad intervals and clears after three good ones")
    func hysteresis() {
        let bad = StreamHealth.Verdict(severity: .warning, title: "bad")
        let good = StreamHealth.Verdict(severity: .normal, title: "good")
        var latch = WarningLatch()
        latch.feed(bad)
        #expect(latch.shown == nil)                  // one noisy second raises nothing
        latch.feed(bad)
        #expect(latch.shown == bad)
        latch.feed(good)
        latch.feed(good)
        #expect(latch.shown == bad)                  // two good ones are not enough
        latch.feed(bad)
        latch.feed(good)
        latch.feed(good)
        #expect(latch.shown == bad)                  // the bad one in between reset the count
        latch.feed(good)
        #expect(latch.shown == nil)
    }

    @Test("an interval's rates come from the difference between two cumulative reports")
    func rates() {
        let before = SessionStats(packetsReceived: 1_000, packetsLost: 10, videoFrames: 600, kbps: 18_000, rttMs: 12)
        let after = SessionStats(packetsReceived: 1_980, packetsLost: 30, videoFrames: 660, kbps: 21_000, rttMs: 15)
        let sample = MetricSample.between(before, after, id: 7)
        #expect(sample.id == 7)
        #expect(sample.fps == 60)
        #expect(sample.kbps == 21_000)
        #expect(sample.rttMs == 15)
        #expect(abs(sample.lossRatio - 20.0 / 1_000.0) < 1e-12)
        #expect(MetricSample.between(after, after, id: 8).lossRatio == 0)   // nothing arrived: no loss, not NaN
    }
}
