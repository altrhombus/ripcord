// The push socket, as a seam: Ripcord.Core.Net's IWebSocketChannel, and URLSessionWebSocketTask behind it.
// The seam is what lets the push channel's dispatch, and the whole rendezvous above it, run against scripted
// frames in the tests with no server.

import Foundation
import Synchronization

public protocol WebSocketChannel: Sendable {
    /// Upgrades. Returns once the server has accepted (the 101), and throws if it refused.
    func connect(to url: URL, headers: [String: String], keepAlive: Duration) async throws
    /// The next text message, or nil when the peer closed. Throws CancellationError when the calling task is
    /// cancelled.
    func receive() async throws -> String?
    /// Best-effort clean close. Never throws.
    func close() async
}

/// The real channel, over URLSessionWebSocketTask.
///
/// Three things differ from .NET's ClientWebSocket, and each is handled here rather than left to surprise:
///
/// - **The keepalive is a loop of ours.** ClientWebSocket's KeepAliveInterval sends frames on a cadence
///   by itself (unsolicited PONGs, in .NET's implementation); URLSessionWebSocketTask sends nothing unless
///   asked. So a task sends a PING every `keepAlive`. A PING rather than a PONG is [X] untested against the
///   push service; RFC 6455 lets a server answer either, and nothing in our captures says which the vendor's
///   WebSocket++ client sends.
/// - **"Connected" is the delegate's didOpen**, not the return of resume(). The session must not be created
///   until the upgrade has succeeded (see PushChannel), so connect() waits for the 101 or the failure.
/// - **The subprotocol is a request header.** The one URLSession call that takes a URLRequest (needed for
///   the X-PSN-* set) has no protocols parameter, and URLSession honours `Sec-WebSocket-Protocol` set on the
///   request instead. [X] against the live front-end; the test pins that the header is set.
public final class URLSessionWebSocketChannel: WebSocketChannel {
    private struct State {
        var session: URLSession?
        var task: URLSessionWebSocketTask?
        var pinger: Task<Void, Never>?
    }

    private let state = Mutex(State())
    private let configuration: URLSessionConfiguration

    public init(configuration: URLSessionConfiguration = .ephemeral) {
        self.configuration = configuration
    }

    public func connect(to url: URL, headers: [String: String], keepAlive: Duration) async throws {
        var request = URLRequest(url: url)
        for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }

        let opened = OneShot<Void>()
        let delegate = OpenDelegate(opened)
        let session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
        let task = session.webSocketTask(with: request)
        state.withLock { $0.session = session; $0.task = task }
        task.resume()

        do {
            try await withTaskCancellationHandler {
                try await opened.value()
            } onCancel: {
                task.cancel(with: .goingAway, reason: nil)
            }
        } catch {
            await close()
            throw error is CancellationError ? error : CloudError.webSocket(String(describing: error))
        }

        if keepAlive > .zero {
            let pinger = Task.detached {
                while !Task.isCancelled {
                    try? await Task.sleep(for: keepAlive)
                    guard !Task.isCancelled else { return }
                    task.sendPing { _ in }
                }
            }
            state.withLock { $0.pinger = pinger }
        }
    }

    public func receive() async throws -> String? {
        guard let task = state.withLock({ $0.task }) else {
            throw CloudError.webSocket("receive before connect")
        }
        while true {
            let message: URLSessionWebSocketTask.Message
            do {
                message = try await withTaskCancellationHandler {
                    try await task.receive()
                } onCancel: {
                    task.cancel(with: .goingAway, reason: nil)
                }
            } catch {
                // An abnormal close (the service's 4101 teardown) and a normal one both read as end of stream,
                // as ClientWebSocketChannel reports them. Cancellation is the caller's own and stays an error.
                if Task.isCancelled { throw CancellationError() }
                return nil
            }
            switch message {
            case .string(let text): return text
            case .data: continue   // not expected on this channel; skipped rather than decoded as text
            @unknown default: continue
            }
        }
    }

    public func close() async {
        let (session, task, pinger) = state.withLock { s in
            defer { s = State() }
            return (s.session, s.task, s.pinger)
        }
        pinger?.cancel()
        task?.cancel(with: .normalClosure, reason: nil)
        session?.invalidateAndCancel()
    }

    /// Reports the upgrade's outcome. URLSession holds its delegate strongly until invalidated, which
    /// close() does.
    private final class OpenDelegate: NSObject, URLSessionWebSocketDelegate, Sendable {
        let opened: OneShot<Void>

        init(_ opened: OneShot<Void>) { self.opened = opened }

        func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask,
                        didOpenWithProtocol protocol: String?) {
            opened.succeed(())
        }

        func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: (any Error)?) {
            let status = (task.response as? HTTPURLResponse)?.statusCode
            opened.fail(CloudError.webSocket(
                "the upgrade did not complete\(status.map { " (HTTP \($0))" } ?? "")"
                    + (error.map { ": \($0.localizedDescription)" } ?? "")))
        }
    }
}
