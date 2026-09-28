// The lifecycle rules, ported from the .NET SessionController tests' cases: the policy on its own, and the
// loop against fake sessions that stream, fail or flap on cue.

@testable import RipcordKit
import Foundation
import Synchronization
import Testing

/// A session that plays a script: optionally reaches streaming, then ends after `lasting`.
private final class FakeSession: SessionHandle, @unchecked Sendable {
    let handlers: SessionHandlers
    let streams: Bool
    let lasting: Duration
    let reason: SessionEndReason
    let disconnects = Mutex(0)

    init(_ h: SessionHandlers, streams: Bool, lasting: Duration, reason: SessionEndReason) {
        handlers = h; self.streams = streams; self.lasting = lasting; self.reason = reason
    }

    func start() {
        Task.detached { [self] in
            if streams { handlers.stage(.streaming) }
            try? await Task.sleep(for: lasting)
            handlers.ended(SessionOutcome(stage: streams ? .streaming : .controlOpen, endReason: reason, controlError: 0, curve: 0))
        }
    }

    func disconnect(restConsole: Bool) { disconnects.withLock { $0 += 1 } }
}

private func quickPolicy() -> SessionPolicy {
    var p = SessionPolicy()
    p.watchdogInterval = .milliseconds(5)
    p.minimumHealthySession = .milliseconds(200)
    p.maxReconnectAttempts = 2
    return p
}

/// Runs a controller until it reaches a terminal status, and returns every status it passed through.
private func runToEnd(policy: SessionPolicy, script: @escaping @Sendable (Int, SessionHandlers) -> FakeSession) async -> [SessionStatus] {
    let seen = Mutex<[SessionStatus]>([])
    let attempts = Mutex(0)
    let controller = SessionController(policy: policy, handlers: SessionHandlers(),
                                       onStatus: { s in seen.withLock { $0.append(s) } },
                                       sleep: { _ in try await Task.sleep(for: .milliseconds(1)) },
                                       factory: { h in script(attempts.withLock { $0 += 1; return $0 }, h) })
    controller.start()
    for _ in 0..<400 {
        if seen.withLock({ $0.last?.isTerminal == true }) { break }
        try? await Task.sleep(for: .milliseconds(10))
    }
    return seen.withLock { $0 }
}

@Suite("Session lifecycle")
struct SessionControllerTests {
    @Test("backoff doubles from one second and caps at fifteen")
    func backoff() {
        let p = SessionPolicy()
        #expect((1...6).map { p.backoff(attempt: $0) } == [.seconds(1), .seconds(2), .seconds(4), .seconds(8), .seconds(15), .seconds(15)])
    }

    @Test("a console still talking keeps a frameless session healthy: a paused game is not a stall")
    func pausedGame() {
        let p = SessionPolicy()
        #expect(p.watchdog(sinceFrame: .seconds(30), sinceConsole: .milliseconds(300), degraded: false) == .keepWatching)
        #expect(p.watchdog(sinceFrame: .seconds(30), sinceConsole: .milliseconds(300), degraded: true) == .recover)
    }

    @Test("a silent console degrades at two seconds without frames and is replaced at six")
    func stall() {
        let p = SessionPolicy()
        #expect(p.watchdog(sinceFrame: .seconds(1), sinceConsole: .seconds(3), degraded: false) == .keepWatching)
        #expect(p.watchdog(sinceFrame: .seconds(3), sinceConsole: .seconds(3), degraded: false) == .degrade)
        #expect(p.watchdog(sinceFrame: .seconds(3), sinceConsole: .seconds(3), degraded: true) == .keepWatching)
        #expect(p.watchdog(sinceFrame: .seconds(7), sinceConsole: .seconds(7), degraded: true) == .replace)
    }

    @Test("an unretryable failure fails at once, without a retry")
    func unretryable() async {
        let seen = await runToEnd(policy: quickPolicy()) { _, h in FakeSession(h, streams: false, lasting: .zero, reason: .refused) }
        #expect(seen.last?.lifecycle == .failed)
        #expect(!seen.contains { $0.lifecycle == .reconnecting })
    }

    @Test("retryable failures give up after the attempt budget")
    func budget() async {
        let seen = await runToEnd(policy: quickPolicy()) { _, h in FakeSession(h, streams: false, lasting: .zero, reason: .timeout) }
        #expect(seen.last?.lifecycle == .failed)
        #expect(seen.last?.detail.hasPrefix("Couldn't reconnect after 2 attempts") == true)
    }

    @Test("a console that accepts and drops every connection is not retried forever")
    func flapping() async {
        let seen = await runToEnd(policy: quickPolicy()) { _, h in FakeSession(h, streams: true, lasting: .milliseconds(10), reason: .consoleClosed) }
        #expect(seen.last?.lifecycle == .failed)
        #expect(seen.last?.detail.contains("dropped each one immediately") == true)
    }

    @Test("a session that lasted resets the budget, so a later failure is retried again")
    func healthyResets() async {
        // Attempts 1 and 2 last past the healthy minimum; then it fails unretryably to end the test.
        let seen = await runToEnd(policy: quickPolicy()) { n, h in
            n <= 3 ? FakeSession(h, streams: true, lasting: .milliseconds(250), reason: .consoleClosed)
                   : FakeSession(h, streams: false, lasting: .zero, reason: .refused)
        }
        #expect(seen.last?.lifecycle == .failed)
        #expect(seen.last?.detail.contains("refused") == true)
        #expect(seen.filter { $0.lifecycle == .streaming }.count >= 3)
    }
}
