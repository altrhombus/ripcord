// The push channel and what it reads: signaling parsed out of push frames, customData1, the console's join,
// and the dispatch loop over a scripted socket. Ported from HalyardSignalingMessageTests.cs and
// HalyardPushChannelTests.cs, same frames, same expectations. The frames are synthetic (documentation and
// private addresses, the .NET tests' placeholder base64) in the envelope shape decoded from cap66/cap67.

import Foundation
@testable import RipcordKit
import Synchronization
import Testing

@Suite("Cloud: signaling messages")
struct SignalingMessageTests {
    @Test("parses the console's OFFER and its candidates")
    func consoleOffer() throws {
        let message = try #require(SignalingMessage.parse(pushFrame: Frames.consoleOffer))
        #expect(message.action == "OFFER")
        #expect(message.reqID == 1)
        #expect(message.fromPlatform == "PROSPERO")
        #expect(message.isConsoleOffer)
        #expect(message.sid == 16531)
        #expect(message.candidates == [SignalingCandidate(type: "STATIC", address: "203.0.113.7", port: 9303),
                                       SignalingCandidate(type: "LOCAL", address: "192.168.1.50", port: 9303)])
    }

    @Test("recovers the skey and hashed id")
    func keys() throws {
        let message = try #require(SignalingMessage.parse(pushFrame: Frames.consoleOffer))
        #expect(message.sessionKey?.count == 16)
        #expect(message.localHashedID?.count == 20)
    }

    @Test("our own echoed OFFER is not mistaken for the console's")
    func ownEcho() throws {
        let own = Frames.consoleOffer.replacingOccurrences(of: #""platform":"PROSPERO""#, with: #""platform":"REMOTE_PLAY""#)
        let message = try #require(SignalingMessage.parse(pushFrame: own))
        #expect(message.fromPlatform == "REMOTE_PLAY")
        #expect(!message.isConsoleOffer)
    }

    @Test("a RESULT has no candidates")
    func result() throws {
        let message = try #require(SignalingMessage.parse(pushFrame: Frames.signaling(
            from: "PROSPERO", #"{"action":"RESULT","reqId":1,"error":0,"connRequest":{}}"#)))
        #expect(message.action == "RESULT")
        #expect(message.candidates.isEmpty)
        #expect(!message.isConsoleOffer)
    }

    @Test("non-signaling notifications are not signaling", arguments: [
        "members:created", "customData1:updated", "remotePlaySession:created",
    ])
    func notSignaling(dataType: String) {
        #expect(SignalingMessage.parse(pushFrame: Frames.signaling(from: "PROSPERO", "{}", dataType: dataType)) == nil)
    }

    @Test("a message on another channel is not ours")
    func otherChannel() {
        #expect(SignalingMessage.parse(pushFrame: Frames.signaling(from: "PROSPERO", Frames.consoleOfferBody,
                                                                   channel: "remote_play:2")) == nil)
    }

    @Test("a PS4's STUN candidate comes through")
    func stunCandidate() throws {
        let message = try #require(SignalingMessage.parse(pushFrame: Frames.signaling(from: "PS4",
            #"{"action":"OFFER","reqId":1,"error":0,"connRequest":{"sid":1,"peerSid":0,"#
            + #""skey":"3DlNoSVIIrMt0LZJUsljaQ==","natType":2,"candidate":["#
            + #"{"type":"STUN","addr":"203.0.113.9","mappedAddr":"0.0.0.0","port":1029,"mappedPort":0}],"#
            + #""localHashedId":"kZ4dwoBU+W1wDpBeejemC+lZ1Eo="}}"#)))
        #expect(message.candidates == [SignalingCandidate(type: "STUN", address: "203.0.113.9", port: 1029)])
    }

    @Test("the console's ACCEPT, with the vendor's malformed localPeerAddr, still parses")
    func malformedAccept() throws {
        let message = try #require(SignalingMessage.parse(pushFrame: Frames.signaling(from: "PROSPERO",
            #"{"action":"ACCEPT","reqId":14,"error":0,"connRequest":{"sid":24043,"#
            + #""peerSid":1,"skey":"3DlNoSVIIrMt0LZJUsljaQ==","natType":0,"candidate":["#
            + #"{"type":"LOCAL","addr":"198.51.100.7","mappedAddr":"198.51.100.9","#
            + #""port":60473,"mappedPort":9303}],"defaultRouteMacAddr":"","#
            + #""localPeerAddr":,"localHashedId":""}}"#)))
        #expect(message.action == "ACCEPT")
        #expect(message.reqID == 14)
        #expect(message.sid == 24043)
        #expect(message.peerSid == 1)
        #expect(message.candidates.map(\.port) == [60473])
        #expect(message.localHashedID == nil)   // empty is absent
    }

    @Test("the console's ACCEPT expects a RESULT, and an ack or our own message does not")
    func expectsResult() throws {
        let accept = Frames.consoleAccept(reqID: 14, sid: 24043)
        let result = Frames.signaling(from: "PROSPERO", #"{"action":"RESULT","reqId":2,"error":0,"connRequest":{}}"#)
        let ours = Frames.signaling(from: "REMOTE_PLAY", #"{"action":"ACCEPT","reqId":2,"error":0,"connRequest":{}}"#)
        #expect(try #require(SignalingMessage.parse(pushFrame: accept)).expectsResult)
        #expect(try !#require(SignalingMessage.parse(pushFrame: result)).expectsResult)
        #expect(try !#require(SignalingMessage.parse(pushFrame: ours)).expectsResult)
        #expect(try #require(SignalingMessage.parse(pushFrame: Frames.consoleOffer)).expectsResult)
    }

    @Test("malformed or incomplete frames are nil", arguments: [
        "", "not json", "{}", #"{"dataType":"psn:sessionManager:sys:rps:sessionMessage:created"}"#,
    ])
    func malformed(frame: String) {
        #expect(SignalingMessage.parse(pushFrame: frame) == nil)
    }

    @Test("customData1 is read out of its notification, and only its")
    func customData1() {
        #expect(CustomDataNotification.customData1(inPushFrame: Frames.customData1("bTNadUEzNy9NcGFic1k5")) == "bTNadUEzNy9NcGFic1k5")
        #expect(CustomDataNotification.customData1(inPushFrame: Frames.customData1("")) == nil)
        #expect(CustomDataNotification.customData1(inPushFrame: Frames.consoleOffer) == nil)
        #expect(CustomDataNotification.customData1(inPushFrame: "garbage") == nil)
    }

    @Test("the console's join is recognised, and our own is not")
    func joined() {
        #expect(CustomDataNotification.isConsoleJoined(pushFrame: Frames.joined()))
        #expect(!CustomDataNotification.isConsoleJoined(pushFrame: Frames.joined(platform: "REMOTE_PLAY")))
        #expect(!CustomDataNotification.isConsoleJoined(pushFrame: Frames.presence))   // members:created, no members
    }
}

@Suite("Cloud: the push channel")
struct PushChannelTests {
    let server = PushServerInfo(fqdn: "abc-pushcl.np.communication.playstation.net",
                                keepAlive: PushKeepAlive(clientKeepAliveIntervalMs: 10_000, clientKeepAliveTimeoutMs: 40_000,
                                                         serverKeepAliveTimeoutMs: 30_000, serverPresenceTimeoutMs: 30_000))

    /// Runs the channel over `socket` (which the test has already closed) and returns what it raised.
    private func run(_ socket: FakeWebSocket, subscribe: (PushChannel, Recorder) -> Void = { channel, recorder in
        channel.subscribe { recorder.add($0) }
    }) async throws -> Recorder {
        let channel = PushChannel(socket: socket)
        let recorder = Recorder()
        subscribe(channel, recorder)
        try await withTimeout(.seconds(5)) { try await channel.run(server: server, accessToken: "test-token") }
        return recorder
    }

    @Test("connects to the push URL with bearer auth and the service's keepalive")
    func connects() async throws {
        let socket = FakeWebSocket()
        socket.finish()
        _ = try await run(socket)
        let connection = try #require(socket.connection)
        #expect(connection.url.absoluteString == "wss://abc-pushcl.np.communication.playstation.net/np/pushNotification")
        #expect(connection.headers["Authorization"] == "Bearer test-token")
        #expect(connection.keepAlive == .seconds(10))
    }

    @Test("sends the upgrade headers and subprotocol the captured handshake has")
    func upgradeHeaders() async throws {
        let socket = FakeWebSocket()
        socket.finish()
        _ = try await run(socket)
        let headers = try #require(socket.connection?.headers)
        #expect(headers["Sec-WebSocket-Protocol"] == "np-pushpacket")
        #expect(headers["X-PSN-APP-TYPE"] == "REMOTE_PLAY")
        #expect(headers["X-PSN-APP-VER"] == "RemotePlay/1.0")
        #expect(headers["X-PSN-PROTOCOL-VERSION"] == "2.1")
        #expect(headers["X-PSN-KEEP-ALIVE-STATUS-TYPE"] == "3")
        #expect(headers["X-PSN-RECONNECTION"] == "false")
        #expect(headers["X-PSN-OS-VER"] != nil)
        #expect(headers["User-Agent"] != nil)
    }

    @Test("surfaces signaling and drops presence")
    func dropsPresence() async throws {
        let socket = FakeWebSocket()
        for frame in [Frames.presence, Frames.consoleOffer, Frames.presence] { socket.push(frame) }
        socket.finish()
        let offers = try await run(socket).signaling
        #expect(offers.count == 1)
        #expect(offers.first?.isConsoleOffer == true)
        #expect(offers.first?.fromPlatform == "PROSPERO")
        #expect(offers.first?.candidates.contains { $0.type == "STATIC" && $0.port == 9303 } == true)
    }

    @Test("customData1 has its own event and is not signaling")
    func customData1() async throws {
        let socket = FakeWebSocket()
        for frame in [Frames.presence, Frames.customData1("bTNadUEzNy9NcGFic1k5"), Frames.consoleOffer] { socket.push(frame) }
        socket.finish()
        let recorder = try await run(socket)
        #expect(recorder.customData1 == ["bTNadUEzNy9NcGFic1k5"])
        #expect(recorder.signaling.count == 1)
        #expect(recorder.frames == 3)
    }

    @Test("the console joining has its own event")
    func consoleJoined() async throws {
        let socket = FakeWebSocket()
        socket.push(Frames.joined(platform: "REMOTE_PLAY"))
        socket.push(Frames.joined())
        socket.finish()
        #expect(try await run(socket).joins == 1)
    }

    @Test("a second subscriber sees everything, and an unsubscribed one nothing more")
    func subscriptions() async throws {
        let socket = FakeWebSocket()
        socket.push(Frames.consoleOffer)
        socket.push(Frames.consoleOffer)
        socket.finish()
        let other = Recorder()
        let recorder = try await run(socket) { channel, recorder in
            channel.subscribe { recorder.add($0) }
            let dropped = channel.subscribe { other.add($0) }
            channel.unsubscribe(dropped)
        }
        #expect(recorder.signaling.count == 2)
        #expect(other.signaling.isEmpty)
    }

    @Test("returns when the peer closes, with no cancellation")
    func peerCloses() async throws {
        let socket = FakeWebSocket()
        socket.push(Frames.consoleOffer)
        socket.finish()
        #expect(try await run(socket).signaling.count == 1)
    }

    @Test("stops when cancelled while the connection is still open")
    func cancelled() async throws {
        let socket = FakeWebSocket()
        socket.push(Frames.consoleOffer)
        let channel = PushChannel(socket: socket)
        let recorder = Recorder()
        channel.subscribe { recorder.add($0) }
        let run = Task { [server] in try await channel.run(server: server, accessToken: "test-token") }
        #expect(await eventually { recorder.signaling.count == 1 })
        run.cancel()
        await #expect(throws: CancellationError.self) { try await run.value }
    }

    @Test("a failed upgrade reaches whoever awaits connected, not only the caller")
    func failedUpgrade() async {
        let channel = PushChannel(socket: FakeWebSocket(failConnect: CloudError.webSocket("HTTP 400")))
        let waiter = Task { try await channel.connected() }
        await #expect(throws: CloudError.self) { try await channel.run(server: server, accessToken: "test-token") }
        await #expect(throws: CloudError.self) { try await waiter.value }
    }

    @Test("the keepalive falls back to ten seconds when the lookup names none")
    func keepaliveFallback() {
        let info = PushServerInfo(fqdn: "host")
        #expect(info.clientKeepAlive == .seconds(10))
        #expect(info.pushURL?.absoluteString == "wss://host/np/pushNotification")
    }
}

/// Collects push events from the receive loop.
final class Recorder: Sendable {
    private let events = Mutex<[PushEvent]>([])

    func add(_ event: PushEvent) { events.withLock { $0.append(event) } }

    var signaling: [SignalingMessage] {
        events.withLock { $0.compactMap { if case .signaling(let m) = $0 { m } else { nil } } }
    }

    var customData1: [String] {
        events.withLock { $0.compactMap { if case .customData1(let v) = $0 { v } else { nil } } }
    }

    var joins: Int { events.withLock { $0.filter { if case .consoleJoined = $0 { true } else { false } }.count } }
    var frames: Int { events.withLock { $0.filter { if case .frame = $0 { true } else { false } }.count } }
}
