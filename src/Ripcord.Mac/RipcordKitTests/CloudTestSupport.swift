// What the cloud tests share: an HTTP stub under URLSession, a scriptable WebSocket, and the push frames the
// .NET tests build. Nothing here reaches the network: the stub answers every request itself, and a test
// session is built with it as its only protocol class.
//
// Values are synthetic throughout: documentation and private addresses, made-up ids, the .NET tests' own
// placeholder base64. No real token, account id, device id or credential appears in any test.

import Foundation
@testable import RipcordKit
import Synchronization
import Testing

// MARK: - HTTP

/// A request as the stub saw it, with the body read back out of its stream.
struct RecordedRequest: Sendable {
    var method: String
    var url: String
    var headers: [String: String]
    var body: String

    func header(_ name: String) -> String? {
        headers.first { $0.key.caseInsensitiveCompare(name) == .orderedSame }?.value
    }
}

/// Answers every request from a handler the running test installs (a negative status is "no answer": the
/// request fails as a dropped connection would). One handler at a time, which is why every
/// suite that uses it lives under `StubbedHTTP`, a serialized suite.
final class StubURLProtocol: URLProtocol, @unchecked Sendable {
    typealias Handler = @Sendable (RecordedRequest) -> (status: Int, body: String)

    private static let state = Mutex<(handler: Handler?, requests: [RecordedRequest])>((nil, []))

    static func install(_ handler: @escaping Handler) {
        state.withLock { $0 = (handler, []) }
    }

    static var requests: [RecordedRequest] { state.withLock { $0.requests } }

    static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: configuration)
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        var body = request.httpBody ?? Data()
        if body.isEmpty, let stream = request.httpBodyStream {
            stream.open()
            var buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable {
                let n = stream.read(&buffer, maxLength: buffer.count)
                if n <= 0 { break }
                body.append(buffer, count: n)
            }
            stream.close()
        }
        let recorded = RecordedRequest(method: request.httpMethod ?? "GET", url: request.url?.absoluteString ?? "",
                                       headers: request.allHTTPHeaderFields ?? [:],
                                       body: String(decoding: body, as: UTF8.self))
        let handler = Self.state.withLock { s -> Handler? in
            s.requests.append(recorded)
            return s.handler
        }
        let (status, text) = handler?(recorded) ?? (500, "no stub installed")
        if status < 0 {   // a request that never got an answer
            client?.urlProtocol(self, didFailWithError: URLError(.notConnectedToInternet))
            return
        }
        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: [:])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(text.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

/// Every suite that installs a StubURLProtocol handler is nested in here, so they run one at a time.
@Suite(.serialized) enum StubbedHTTP {}

/// A token provider already holding a live access token, for tests of the REST surface.
func seededTokens(session: URLSession, accessToken: String = "access-1") async -> TokenProvider {
    let provider = TokenProvider(auth: AuthClient(session: session, config: testConfig))
    await provider.seed(AccountTokens(accessToken: accessToken, refreshToken: "refresh-1",
                                      expiresAt: Date().addingTimeInterval(3600)))
    return provider
}

let testConfig = ClientConfig(clientID: "test-client", clientSecret: "test-secret")

/// The synthetic device id the .NET tests use: the bytes 0 through 15.
let syntheticDeviceID: [UInt8] = Array(0..<16)

/// Its hex, written independently of the code under test.
let syntheticDeviceIDHex = syntheticDeviceID.map { String(format: "%02x", $0) }.joined()

// MARK: - WebSocket

/// A scripted push socket: frames are queued, `finish()` is the peer closing, and `connectGate` holds the
/// upgrade open to test what waits for it.
final class FakeWebSocket: WebSocketChannel {
    struct Connection: Sendable {
        var url: URL
        var headers: [String: String]
        var keepAlive: Duration
    }

    private let frames = Mailbox<String?>()
    private let state = Mutex<(connection: Connection?, closed: Bool, finished: Bool)>((nil, false, false))
    let connectGate: OneShot<Void>?
    let failConnect: (any Error)?

    init(gated: Bool = false, failConnect: (any Error)? = nil) {
        connectGate = gated ? OneShot<Void>() : nil
        self.failConnect = failConnect
    }

    var connection: Connection? { state.withLock { $0.connection } }
    var isConnected: Bool { connection != nil }
    var wasClosed: Bool { state.withLock { $0.closed } }

    func push(_ frame: String) { frames.post(frame) }
    func finish() { frames.post(nil) }

    func connect(to url: URL, headers: [String: String], keepAlive: Duration) async throws {
        if let connectGate { try await connectGate.value() }
        if let failConnect { throw failConnect }
        state.withLock { $0.connection = Connection(url: url, headers: headers, keepAlive: keepAlive) }
    }

    /// Once the peer has closed, every later receive is nil too, as a completed .NET Channel reads.
    func receive() async throws -> String? {
        if state.withLock({ $0.finished }) { return nil }
        let frame = try await frames.next()
        if frame == nil { state.withLock { $0.finished = true } }
        return frame
    }

    func close() async { state.withLock { $0.closed = true } }
}

// MARK: - Push frames, as the .NET tests build them

enum Frames {
    /// A sessionMessage notification carrying `innerBody` (a JSON document, as text) in its payload. The
    /// payload string is escaped by a real JSON writer, so the double nesting is exactly the captured shape.
    static func signaling(from platform: String, _ innerBody: String,
                          dataType: String = "sessionMessage:created", channel: String = "remote_play:1") -> String {
        let payload = WireJSON.quoted("ver=1.0, type=text, body=" + innerBody)
        return #"{"version":"2.1","method":3001,"dataType":"psn:sessionManager:sys:rps:"#
            + dataType + #"","to":{"accountId":1,"onlineId":"tester","platform":["REMOTE_PLAY"]},"#
            + #""body":{"data":{"customProperties":{"from":{"accountId":"1","platform":""#
            + platform + #""}},"sessionId":"00000000-0000-0000-0000-000000000000","#
            + #""sessionMessage":{"channel":""# + channel + #"","payload":"# + payload + "}}}}"
    }

    /// HalyardSignalingMessageTests.ConsoleOffer's inner body.
    static let consoleOfferBody =
        #"{"action":"OFFER","reqId":1,"error":0,"connRequest":{"sid":16531,"peerSid":0,"#
        + #""skey":"3DlNoSVIIrMt0LZJUsljaQ==","natType":2,"candidate":["#
        + #"{"type":"STATIC","addr":"203.0.113.7","mappedAddr":"0.0.0.0","port":9303,"mappedPort":0},"#
        + #"{"type":"LOCAL","addr":"192.168.1.50","mappedAddr":"0.0.0.0","port":9303,"mappedPort":0}],"#
        + #""defaultRouteMacAddr":"00:11:22:33:44:55","#
        + #""localPeerAddr":{"accountId":"1","platform":"PROSPERO"},"#
        + #""localHashedId":"kZ4dwoBU+W1wDpBeejemC+lZ1Eo="}}"#

    static var consoleOffer: String { signaling(from: "PROSPERO", consoleOfferBody) }

    /// A console OFFER with one candidate, and the hashed id and sid the account route keys on.
    static func accountOffer(hashedID: [UInt8], sid: Int, address: String = "192.168.1.50", from platform: String = "PROSPERO") -> String {
        signaling(from: platform,
                  #"{"action":"OFFER","reqId":1,"error":0,"connRequest":{"sid":"# + String(sid) + #","peerSid":0,"#
                  + #""skey":"AAAAAAAAAAAAAAAAAAAAAA==","natType":2,"#
                  + #""candidate":[{"type":"LOCAL","addr":""# + address + #"","mappedAddr":"0.0.0.0","#
                  + #""port":9303,"mappedPort":0}],"#
                  + #""localHashedId":""# + Data(hashedID).base64EncodedString() + #""}}"#)
    }

    static func consoleAccept(reqID: Int, sid: Int) -> String {
        signaling(from: "PROSPERO",
                  #"{"action":"ACCEPT","reqId":"# + String(reqID) + #","error":0,"connRequest":{"sid":"# + String(sid)
                  + #","peerSid":1,"localPeerAddr":,"localHashedId":""}}"#)
    }

    static let presence =
        #"{"version":"2.1","method":3001,"dataType":"psn:sessionManager:sys:rps:members:created","#
        + #""to":{"accountId":1,"onlineId":"tester","platform":["REMOTE_PLAY"]},"body":{"data":{}}}"#

    static func customData1(_ value: String) -> String {
        #"{"version":"2.1","method":3001,"dataType":"psn:sessionManager:sys:rps:customData1:updated","#
            + #""to":{"accountId":1,"onlineId":"tester","platform":["REMOTE_PLAY"]},"#
            + #""body":{"data":{"customData1":""# + value + #""}}}"#
    }

    static func joined(platform: String = "PROSPERO") -> String {
        #"{"dataType":"psn:sessionManager:sys:rps:members:created","body":{"data":{"#
            + #""members":[{"platform":""# + platform + #""}]}}}"#
    }
}

/// Polls until `condition` holds or `timeout` passes. For effects a flow deliberately does not await (the
/// acks go out from a task of their own), where waiting on the effect is the only honest test.
func eventually(timeout: Duration = .seconds(5), _ condition: @Sendable () -> Bool) async -> Bool {
    let deadline = ContinuousClock.now.advanced(by: timeout)
    while ContinuousClock.now < deadline {
        if condition() { return true }
        try? await Task.sleep(for: .milliseconds(5))
    }
    return condition()
}
