// The persistent push connection: the WebSocket the console's signaling arrives on. Ported from
// HalyardPushChannel.cs.
//
// The inbound half of the rendezvous. The client POSTs its OFFER through the REST surface; the console's
// OFFER, carrying the candidates the client cannot otherwise learn, comes back here in a session-manager
// notification, and so do the account-pairing seed (customData1) and the console's join. Everything else
// (presence, other members) is dropped.
//
// EVENTS ARE SUBSCRIPTIONS. The .NET type raises C# events; the Swift one keeps a list of handlers under a
// lock and calls them in order on the receive loop. They must be quick, and they cannot throw (a Swift
// closure without `throws` cannot), which is the guarantee .NET buys by catching around each handler: a bad
// consumer cannot take the connection, and the connect attempt, down with it.

import Foundation
import Synchronization

public enum PushEvent: Sendable {
    /// Every text frame, before parsing. For diagnostics and capture.
    case frame(String)
    /// A signaling message from either peer: OFFER, ACCEPT, RESULT.
    case signaling(SignalingMessage)
    /// The raw (double-base64) `customData1`: the account-pairing seed, still encrypted.
    case customData1(String)
    /// The console has joined the session.
    case consoleJoined
}

public final class PushChannel: Sendable {
    public struct Subscription: Hashable, Sendable {
        fileprivate let id: UInt64
    }

    private struct State {
        var handlers: [(id: UInt64, handler: @Sendable (PushEvent) -> Void)] = []
        var nextID: UInt64 = 0
    }

    private let socket: any WebSocketChannel
    private let state = Mutex(State())
    private let connectedSignal = OneShot<Void>()

    public init(socket: any WebSocketChannel) { self.socket = socket }

    @discardableResult
    public func subscribe(_ handler: @escaping @Sendable (PushEvent) -> Void) -> Subscription {
        state.withLock { s in
            s.nextID += 1
            s.handlers.append((s.nextID, handler))
            return Subscription(id: s.nextID)
        }
    }

    public func unsubscribe(_ subscription: Subscription) {
        state.withLock { $0.handlers.removeAll { $0.id == subscription.id } }
    }

    /// Returns once the upgrade has succeeded, and throws if it failed. Whoever must not act before the push
    /// connection exists awaits this: the session's message channel binds to the account's live push
    /// connection when the session is created, so a session created first has no `sessionMessage` resource
    /// and every OFFER to it 404s.
    public func connected() async throws { try await connectedSignal.value() }

    /// The upgrade headers, matched field for field to a captured vendor handshake (cap68). The
    /// Sec-WebSocket-Key, Version, Upgrade and Connection headers are the socket's own and are not set here.
    public static func upgradeHeaders(accessToken: String) -> [String: String] {
        [
            "Authorization": "Bearer \(accessToken)",
            "Sec-WebSocket-Protocol": PushHeaders.subProtocol,
            "User-Agent": PushHeaders.userAgent,
            "X-PSN-APP-TYPE": PushHeaders.appType,
            "X-PSN-APP-VER": PushHeaders.appVersion,
            "X-PSN-OS-VER": PushHeaders.osVersion,
            "X-PSN-PROTOCOL-VERSION": PushHeaders.protocolVersion,
            "X-PSN-KEEP-ALIVE-STATUS-TYPE": PushHeaders.keepAliveStatusType,
            "X-PSN-RECONNECTION": PushHeaders.reconnection,
        ]
    }

    /// Connects, then pumps frames until the peer closes (a normal return, as at session teardown) or the
    /// calling task is cancelled (a CancellationError). A caller can await the channel's lifetime directly.
    ///
    /// - Parameter accessToken: sent as `Authorization: Bearer` on the upgrade, as a captured vendor
    ///   handshake does (cap68).
    public func run(server: PushServerInfo, accessToken: String) async throws {
        guard !accessToken.isEmpty else { throw CloudError.invalidArgument("accessToken must not be empty") }
        guard let url = server.pushURL else { throw CloudError.invalidArgument("no push host") }

        do {
            try await socket.connect(to: url, headers: Self.upgradeHeaders(accessToken: accessToken),
                                     keepAlive: server.clientKeepAlive)
        } catch {
            // Anyone awaiting connected() learns of the failure too, not only this call's caller.
            connectedSignal.fail(error)
            throw error
        }
        connectedSignal.succeed(())

        while !Task.isCancelled {
            guard let frame = try await socket.receive() else { break }   // peer closed
            dispatch(frame)
        }
    }

    /// Closes the socket. The .NET DisposeAsync.
    public func close() async { await socket.close() }

    func dispatch(_ frame: String) {
        let handlers = state.withLock { $0.handlers.map(\.handler) }
        guard !handlers.isEmpty else { return }
        func raise(_ event: PushEvent) { for handler in handlers { handler(event) } }

        raise(.frame(frame))
        // A frame is signaling, customData1 or a join, never two of them.
        if let message = SignalingMessage.parse(pushFrame: frame) {
            raise(.signaling(message))
        } else if let customData1 = CustomDataNotification.customData1(inPushFrame: frame) {
            raise(.customData1(customData1))
        } else if CustomDataNotification.isConsoleJoined(pushFrame: frame) {
            raise(.consoleJoined)
        }
    }
}
