// A Mutex that closures on other threads can share: Mutex itself is non-copyable, so it cannot be captured.

import Synchronization

final class Locked<Value>: @unchecked Sendable {
    private let mutex: Mutex<Value>

    init(_ value: sending Value) { mutex = Mutex(value) }

    func withLock<R>(_ body: (inout sending Value) throws -> sending R) rethrows -> sending R {
        try mutex.withLock(body)
    }
}
