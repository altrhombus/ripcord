// The three waiting shapes the cloud tier needs, and nothing more: a value that arrives once (the .NET side's
// TaskCompletionSource), a queue of values read in order (its Channel<T>), and "this, or give up after that
// long" (its WaitAsync(TimeSpan)).
//
// Written here rather than borrowed from a package because the rules forbid one, and because each is small
// enough to read whole. Both waiters are cancellation-aware: a Task that is cancelled while waiting is
// resumed with CancellationError at once, and its continuation removed, so a timeout (which cancels the
// losing child) never leaves a continuation behind.

import Synchronization

/// Raised by `withTimeout` when the deadline passes first. The .NET side's TimeoutException.
public struct TimeoutError: Error, Sendable, CustomStringConvertible {
    public let after: Duration
    public var description: String { "timed out after \(after)" }
}

/// Runs `operation`, or throws `TimeoutError` once `duration` has passed. Whichever finishes second is
/// cancelled, which is what releases a waiter blocked in `OneShot.value()` or `Mailbox.next()`.
func withTimeout<T: Sendable>(_ duration: Duration,
                              _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { group in
        group.addTask { try await operation() }
        group.addTask {
            try await Task.sleep(for: duration)
            throw TimeoutError(after: duration)
        }
        defer { group.cancelAll() }
        guard let first = try await group.next() else { throw CancellationError() }
        return first
    }
}

/// A value that is set once and awaited by anyone. The first `succeed` or `fail` wins; later ones report
/// false, which is what the .NET code's TrySetResult is used for (a repeated OFFER must not replace the first).
final class OneShot<Value: Sendable>: Sendable {
    private struct State {
        var outcome: Result<Value, any Error>?
        var waiters: [UInt64: CheckedContinuation<Value, any Error>] = [:]
        var cancelled: Set<UInt64> = []
        var nextID: UInt64 = 0
    }

    private let state = Mutex(State())

    init() {}

    @discardableResult
    func succeed(_ value: Value) -> Bool { complete(.success(value)) }

    @discardableResult
    func fail(_ error: any Error) -> Bool { complete(.failure(error)) }

    var isCompleted: Bool { state.withLock { $0.outcome != nil } }

    /// The value, if it has arrived. Never waits.
    var current: Value? { state.withLock { try? $0.outcome?.get() } }

    func value() async throws -> Value {
        let id = state.withLock { s -> UInt64 in
            s.nextID += 1
            return s.nextID
        }
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Value, any Error>) in
                let immediate = state.withLock { s -> Result<Value, any Error>? in
                    if let outcome = s.outcome { return outcome }
                    if s.cancelled.remove(id) != nil { return .failure(CancellationError()) }
                    s.waiters[id] = continuation
                    return nil
                }
                if let immediate { continuation.resume(with: immediate) }
            }
        } onCancel: {
            let waiter = state.withLock { s -> CheckedContinuation<Value, any Error>? in
                if let waiter = s.waiters.removeValue(forKey: id) { return waiter }
                s.cancelled.insert(id)   // cancelled before the continuation was registered
                return nil
            }
            waiter?.resume(throwing: CancellationError())
        }
    }

    private func complete(_ outcome: Result<Value, any Error>) -> Bool {
        let waiters = state.withLock { s -> [CheckedContinuation<Value, any Error>]? in
            guard s.outcome == nil else { return nil }
            s.outcome = outcome
            defer { s.waiters.removeAll() }
            return Array(s.waiters.values)
        }
        guard let waiters else { return false }
        for waiter in waiters { waiter.resume(with: outcome) }
        return true
    }
}

/// An unbounded queue read in order: the .NET side's `Channel.CreateUnbounded`. Values posted before anyone
/// reads are kept; a reader that arrives first waits.
final class Mailbox<Value: Sendable>: Sendable {
    private struct State {
        var buffer: [Value] = []
        var waiters: [(id: UInt64, continuation: CheckedContinuation<Value, any Error>)] = []
        var cancelled: Set<UInt64> = []
        var nextID: UInt64 = 0
    }

    private let state = Mutex(State())

    init() {}

    func post(_ value: Value) {
        let waiter = state.withLock { s -> CheckedContinuation<Value, any Error>? in
            if s.waiters.isEmpty {
                s.buffer.append(value)
                return nil
            }
            return s.waiters.removeFirst().continuation
        }
        waiter?.resume(returning: value)
    }

    func next() async throws -> Value {
        let id = state.withLock { s -> UInt64 in
            s.nextID += 1
            return s.nextID
        }
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Value, any Error>) in
                let immediate = state.withLock { s -> Result<Value, any Error>? in
                    if !s.buffer.isEmpty { return .success(s.buffer.removeFirst()) }
                    if s.cancelled.remove(id) != nil { return .failure(CancellationError()) }
                    s.waiters.append((id, continuation))
                    return nil
                }
                if let immediate { continuation.resume(with: immediate) }
            }
        } onCancel: {
            let waiter = state.withLock { s -> CheckedContinuation<Value, any Error>? in
                if let index = s.waiters.firstIndex(where: { $0.id == id }) {
                    return s.waiters.remove(at: index).continuation
                }
                s.cancelled.insert(id)
                return nil
            }
            waiter?.resume(throwing: CancellationError())
        }
    }
}
