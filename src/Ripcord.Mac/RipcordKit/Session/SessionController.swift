// A session's whole life: connect, watch, reconnect with backoff, give up honestly. Ported from the .NET
// client's Ripcord.Client.SessionController and SessionLifecycle, which the Windows app ships. Every rule
// here answers something observed on hardware, and each keeps the reason next to it:
//
//   - Only a session that LASTED resets the retry budget (MinimumHealthySession, 10 s). A console on its
//     way into rest completes the handshake and drops it at once; counting that as success reset the
//     budget every time, and the Windows app reconnected forever.
//   - A stalled PICTURE is not a stalled SESSION. A paused game sends no frames while the console is fine,
//     so the console's own activity decides: still talking means healthy, whatever the frames say. Only
//     silence from the console lets the frame clock degrade the session (2 s) and then replace it (6 s).
//   - A flap backs off too. Reconnecting instantly into a console that is shutting down made the loop
//     tight rather than slow.
//   - An unretryable failure fails at once. C gives the end reason as a value, so the .NET string match
//     ("rejected (403", "registkey", ...) becomes a switch.
//
// The policy is pure and tested on its own; the controller drives any SessionHandle, so the loop is tested
// against fakes without a console.

import Foundation
import Synchronization

public enum SessionLifecycle: Sendable, Equatable {
    case idle, connecting, streaming, degraded, reconnecting, failed, closed
}

public struct SessionStatus: Sendable, Equatable {
    public let lifecycle: SessionLifecycle
    public let detail: String
    public var reconnectAttempt = 0
    public var nextRetryIn: Duration?

    public init(lifecycle: SessionLifecycle, detail: String, reconnectAttempt: Int = 0, nextRetryIn: Duration? = nil) {
        self.lifecycle = lifecycle
        self.detail = detail
        self.reconnectAttempt = reconnectAttempt
        self.nextRetryIn = nextRetryIn
    }

    public var isLive: Bool { lifecycle == .streaming || lifecycle == .degraded }
    public var isTerminal: Bool { lifecycle == .failed || lifecycle == .closed }
}

public struct SessionPolicy: Sendable {
    public var stallTimeout: Duration = .seconds(2)
    public var reconnectAfterStall: Duration = .seconds(6)
    public var watchdogInterval: Duration = .milliseconds(500)
    public var initialBackoff: Duration = .seconds(1)
    public var maxBackoff: Duration = .seconds(15)
    public var maxReconnectAttempts = 6
    public var minimumHealthySession: Duration = .seconds(10)

    public init() {}

    /// Exponential, capped: 1, 2, 4, 8, 15, 15 s.
    public func backoff(attempt: Int) -> Duration {
        let seconds = Double(initialBackoff.components.seconds) * pow(2, Double(max(0, attempt - 1)))
        return min(.seconds(seconds), maxBackoff)
    }

    public enum Verdict: Equatable, Sendable { case keepWatching, degrade, recover, replace }

    /// One watchdog tick. `sinceConsole` is nil before the console has said anything.
    public func watchdog(sinceFrame: Duration, sinceConsole: Duration?, degraded: Bool) -> Verdict {
        if let quiet = sinceConsole, quiet < stallTimeout {
            return degraded ? .recover : .keepWatching   // healthy but perhaps idle: a paused game
        }
        if sinceFrame >= reconnectAfterStall { return .replace }
        if sinceFrame >= stallTimeout { return degraded ? .keepWatching : .degrade }
        return degraded ? .recover : .keepWatching
    }

    /// Whether another attempt could possibly help.
    public func isRetryable(_ reason: SessionEndReason) -> Bool {
        switch reason {
        case .signInCancelled, .signInRejected, .refused, .userDisconnect, .hostCancel: false
        case .none, .consoleClosed, .channelError, .signInNoSession, .timeout, .noMedia, .rendezvousFailed: true
        }
    }
}

/// What the controller needs from a session. ConsoleSession is the real one.
public protocol SessionHandle: Sendable {
    func start()
    func disconnect(restConsole: Bool)
}

extension ConsoleSession: SessionHandle {}

public final class SessionController: Sendable {
    public typealias Factory = @Sendable (SessionHandlers) -> any SessionHandle

    private let policy: SessionPolicy
    private let factory: Factory
    private let userHandlers: SessionHandlers
    private let onStatus: @Sendable (SessionStatus) -> Void
    private let sleep: @Sendable (Duration) async throws -> Void
    private let state = Mutex(State())

    struct State {
        var status = SessionStatus(lifecycle: .idle, detail: "")
        var current: (any SessionHandle)?
        var stopped = false
        var restOnStop = false
        var task: Task<Void, Never>?
    }

    /// `handlers` receive the media and stats of whichever session is live; `ended` and `stage` are the
    /// controller's to interpret, and are still forwarded.
    public init(policy: SessionPolicy = SessionPolicy(), handlers: SessionHandlers,
                onStatus: @escaping @Sendable (SessionStatus) -> Void,
                sleep: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) },
                factory: @escaping Factory) {
        self.policy = policy
        self.userHandlers = handlers
        self.onStatus = onStatus
        self.sleep = sleep
        self.factory = factory
    }

    public var status: SessionStatus { state.withLock { $0.status } }

    public func start() {
        let task = Task.detached { [self] in await run() }
        state.withLock { $0.task = task }
    }

    /// Ends the session for good. `restConsole` only when a person chose it.
    public func stop(restConsole: Bool = false) {
        let current = state.withLock { s -> (any SessionHandle)? in
            s.stopped = true
            s.restOnStop = restConsole
            s.task?.cancel()
            return s.current
        }
        current?.disconnect(restConsole: restConsole)
    }

    private var stopped: Bool { state.withLock { $0.stopped } }

    private func transition(_ lifecycle: SessionLifecycle, _ detail: String, attempt: Int = 0, retryIn: Duration? = nil) {
        let status = SessionStatus(lifecycle: lifecycle, detail: detail, reconnectAttempt: attempt, nextRetryIn: retryIn)
        state.withLock { $0.status = status }
        onStatus(status)
    }

    // MARK: - The loop (SessionController.RunAsync)

    private func run() async {
        var attempt = 0
        var lastReason: String?
        while !stopped {
            let retry = attempt > 0
            transition(retry ? .reconnecting : .connecting,
                       retry ? "Attempt \(attempt) of \(policy.maxReconnectAttempts)." + (lastReason.map { " \($0)" } ?? "")
                             : "Connecting to your console…",
                       attempt: retry ? attempt : 0)

            let run = await runOneSession()
            if stopped { break }

            if run.streamed {
                if run.streamedFor >= policy.minimumHealthySession {
                    attempt = 1
                    lastReason = run.detail
                    continue
                }
                attempt += 1
                if attempt > policy.maxReconnectAttempts {
                    transition(.failed, "The console accepted \(policy.maxReconnectAttempts) connections and dropped each one immediately. It may be going into rest mode — wake it and try again.")
                    return
                }
                lastReason = "The stream dropped as soon as it started."
                let backoff = policy.backoff(attempt: attempt)
                transition(.reconnecting, "The stream dropped straight away. Retrying in \(backoff.components.seconds)s…", attempt: attempt, retryIn: backoff)
                if (try? await sleep(backoff)) == nil { break }
                continue
            }

            guard policy.isRetryable(run.outcome.endReason) else {
                transition(.failed, run.detail)
                return
            }
            attempt += 1
            if attempt > policy.maxReconnectAttempts {
                transition(.failed, "Couldn't reconnect after \(policy.maxReconnectAttempts) attempts. \(run.detail)")
                return
            }
            lastReason = run.detail
            let backoff = policy.backoff(attempt: attempt)
            transition(.reconnecting, "Connection failed. Retrying in \(backoff.components.seconds)s… (\(run.detail))", attempt: attempt, retryIn: backoff)
            if (try? await sleep(backoff)) == nil { break }
        }
        transition(.closed, "Disconnected.")
    }

    private struct RunResult {
        var outcome = SessionOutcome(stage: .idle, endReason: .none, controlError: 0, curve: 0)
        var streamed = false
        var streamedFor: Duration = .zero
        var detail: String { Self.describe(outcome) }

        static func describe(_ o: SessionOutcome) -> String {
            switch o.endReason {
            case .signInRejected: "The console refused the passcode."
            case .signInCancelled: "Sign-in was cancelled."
            case .signInNoSession: "Signed in, but the console never offered a session."
            case .refused: "The console refused the session. It may need pairing again."
            case .timeout: "The console stopped answering while connecting (at \(o.stage))."
            case .consoleClosed: "The console closed the session."
            case .channelError: "The connection failed (at \(o.stage))."
            case .userDisconnect, .hostCancel: "Disconnected."
            case .noMedia: "The console never opened its audio and video connection."
            case .rendezvousFailed: "Could not reach the console through the account" + (o.failure.map { ": \($0)" } ?? ".")
            case .none: "The session ended."
            }
        }
    }

    /// Runs one session to its end, watching it while it streams.
    private func runOneSession() async -> RunResult {
        struct Live { var streamingSince: ContinuousClock.Instant?; var sinceFrame: Duration = .zero
                      var sinceConsole: Duration?; var ended: SessionOutcome? }
        let live = Mutex(Live())

        var handlers = userHandlers
        let forward = userHandlers
        handlers.stage = { stage in
            if stage == .streaming { live.withLock { $0.streamingSince = ContinuousClock.now } }
            forward.stage(stage)
        }
        handlers.stats = { s in
            live.withLock {
                $0.sinceFrame = .milliseconds(Int64(s.msSinceVideoFrame))
                $0.sinceConsole = .milliseconds(Int64(s.msSinceConsoleActivity))
            }
            forward.stats(s)
        }
        handlers.ended = { outcome in
            live.withLock { $0.ended = outcome }
            forward.ended(outcome)
        }

        let session = factory(handlers)
        let alreadyStopped = state.withLock { s -> Bool in
            if !s.stopped { s.current = session }
            return s.stopped
        }
        if alreadyStopped { return RunResult() }
        session.start()

        var announcedStreaming = false
        var degraded = false
        var replaced = false
        while true {
            let snapshot = live.withLock { $0 }
            if let outcome = snapshot.ended {
                state.withLock { $0.current = nil }
                var r = RunResult(outcome: outcome)
                if let since = snapshot.streamingSince {
                    r.streamed = true
                    r.streamedFor = ContinuousClock.now - since
                }
                if replaced { r.outcome = SessionOutcome(stage: outcome.stage, endReason: .timeout, controlError: 0, curve: outcome.curve) }
                return r
            }
            if snapshot.streamingSince != nil, !replaced {
                if !announcedStreaming { transition(.streaming, "Connected."); announcedStreaming = true }
                switch policy.watchdog(sinceFrame: snapshot.sinceFrame, sinceConsole: snapshot.sinceConsole, degraded: degraded) {
                case .keepWatching: break
                case .degrade: degraded = true; transition(.degraded, "Video has stopped — trying to recover…")
                case .recover: degraded = false; transition(.streaming, "Connected.")
                case .replace: replaced = true; session.disconnect(restConsole: false)
                }
            }
            do { try await sleep(policy.watchdogInterval) } catch {
                // Cancelled by stop(): the session is already told to go, so wait for it to end without
                // spinning (a cancelled task's sleeps return at once).
                if stopped { session.disconnect(restConsole: state.withLock { $0.restOnStop }) }
                await uncancellablePause(.milliseconds(50))
            }
        }
    }
}

/// A pause that cancellation does not cut short.
private func uncancellablePause(_ d: Duration) async {
    await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in
        let seconds = Double(d.components.seconds) + Double(d.components.attoseconds) / 1e18
        DispatchQueue.global().asyncAfter(deadline: .now() + seconds) { c.resume() }
    }
}
