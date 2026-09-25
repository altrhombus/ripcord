// The authenticated cloud REST surface: account info, the console list, the session-manager lifecycle
// (create, read, leave), the wake command, and the signaling channel. Ported from HalyardCloudClient.cs;
// structure and ordering from docs/protocol/ps5-cloud-session-api.md.
//
// Every request body is built with WireJSON so it is the .NET body byte for byte (see WireEncoding.swift
// for why that is the target, and why JSONEncoder cannot reach it). The builders are separate static
// functions so the tests can pin each body without a network.

import Foundation

public final class CloudClient: Sendable {
    let http: CloudHTTP
    let tokens: TokenProvider

    public init(session: URLSession = .ripcordCloud, tokens: TokenProvider) {
        self.http = CloudHTTP(session: session)
        self.tokens = tokens
    }

    public func accountInfo() async throws(CloudError) -> AccountInfo {
        try await getJSON(AccountInfo.self, CloudEndpoints.accountInfo)
    }

    /// The push front-end host and its keepalive timing (the serveraddr call).
    public func pushServer() async throws(CloudError) -> PushServerInfo {
        try await getJSON(PushServerInfo.self, CloudEndpoints.pushServerAddress)
    }

    public func listConsoles() async throws(CloudError) -> [CloudConsole] {
        let url = "\(CloudEndpoints.consoleList)?platform=\(CloudEndpoints.currentGenPlatformTag)&includeFields=device&limit=10&offset=0"
        return try await getJSON(ConsoleListResponse.self, url).clients ?? []
    }

    /// Creates (joins) an account-scoped session. The response carries its id.
    public func createSession(pushContextID: String) async throws(CloudError) -> CloudSession {
        let body = try await sendJSON("POST", CloudEndpoints.sessions, Self.createSessionBody(pushContextID: pushContextID))
        guard let parsed = try? JSONDecoder().decode(SessionsResponse.self, from: body),
              let first = parsed.remotePlaySessions?.first else {
            throw .invalidResponse("Session create returned no session.", body: String(decoding: body, as: UTF8.self))
        }
        return first
    }

    public func sessions() async throws(CloudError) -> [CloudSession] {
        try await getJSON(SessionsResponse.self, CloudEndpoints.sessions).remotePlaySessions ?? []
    }

    /// Reads one session back by id. Carries the session-ids header the service requires on a read; a bare
    /// `GET remotePlaySessions` without it is a 400.
    public func session(id sessionID: String) async throws(CloudError) -> [CloudSession] {
        var request = try Self.request("GET", CloudEndpoints.sessions)
        request.setValue(sessionID, forHTTPHeaderField: CloudEndpoints.sessionIDsHeader)
        let body = try await send(request)
        if body.isEmpty { return [] }
        do {
            return try JSONDecoder().decode(SessionsResponse?.self, from: body)?.remotePlaySessions ?? []
        } catch {
            throw .invalidResponse("Session readback could not be parsed.", body: String(decoding: body, as: UTF8.self))
        }
    }

    public func leaveSession(id sessionID: String) async throws(CloudError) {
        _ = try await send(Self.request("DELETE", "\(CloudEndpoints.sessions)/\(sessionID)/members/me"))
    }

    /// Sends the wake/trigger command to the console named by `consoleDUID`. Returns the command id.
    @discardableResult
    public func sendConnectCommand(consoleDUID: String, accountID: String, sessionID: String, clientType: String,
                                   seeds: ConnectSeeds) async throws(CloudError) -> String {
        let body = try await sendJSON("POST", CloudEndpoints.commands,
                                      Self.connectCommandBody(consoleDUID: consoleDUID, accountID: accountID,
                                                              sessionID: sessionID, clientType: clientType, seeds: seeds))
        guard let parsed = try? JSONDecoder().decode(CommandResponse.self, from: body), let id = parsed.commandId else {
            throw .invalidResponse("Connect command returned no commandId.", body: String(decoding: body, as: UTF8.self))
        }
        return id
    }

    /// Sends a signaling OFFER. `sid` and `reqID` are the caller's because a session offers more than one
    /// connection: the captured client numbers them 1 and 2, with reqIds 1 and 3, and two offers both
    /// claiming stream 1 made the second connection collide with the first.
    public func sendOffer(sessionID: String, accountID: String, consoleDUID: String, candidates: [SignalingCandidate],
                          localHashedID: [UInt8] = [], reqID: Int = 1, sid: Int = 1) async throws(CloudError) {
        let body = Self.offerBody(accountID: accountID, candidates: candidates, localHashedID: localHashedID,
                                  reqID: reqID, sid: sid)
        try await sendSignaling(sessionID: sessionID, accountID: accountID, consoleDUID: consoleDUID, body: body)
    }

    /// Acknowledges a message the peer sent, with a RESULT carrying the *sender's* reqId.
    public func sendResult(sessionID: String, accountID: String, consoleDUID: String, reqID: Int) async throws(CloudError) {
        try await sendSignaling(sessionID: sessionID, accountID: accountID, consoleDUID: consoleDUID,
                                body: Self.resultBody(reqID: reqID))
    }

    /// Answers the console's OFFER, naming its stream id as our `peerSid`. See `acceptBody` for the two things
    /// about it that look wrong and are not.
    public func sendAccept(sessionID: String, accountID: String, consoleDUID: String, reqID: Int, sid: Int, peerSid: Int,
                           consoleCandidate: SignalingCandidate, localAddress: String, localPort: Int) async throws(CloudError) {
        try await sendSignaling(sessionID: sessionID, accountID: accountID, consoleDUID: consoleDUID,
                                body: Self.acceptBody(reqID: reqID, sid: sid, peerSid: peerSid,
                                                      consoleCandidate: consoleCandidate,
                                                      localAddress: localAddress, localPort: localPort))
    }

    private func sendSignaling(sessionID: String, accountID: String, consoleDUID: String, body: String) async throws(CloudError) {
        let envelope = Self.signalingEnvelope(accountID: accountID, consoleDUID: consoleDUID, body: body)
        _ = try await sendJSON("POST", "\(CloudEndpoints.sessions)/\(sessionID)/sessionMessage", envelope)
    }

    // MARK: - Bodies, byte for byte the .NET ones

    static func createSessionBody(pushContextID: String) -> WireJSON {
        .object([("remotePlaySessions", .array([.object([("members", .array([.object([
            ("accountId", "me"),
            ("deviceUniqueId", "me"),
            ("platform", "me"),
            ("pushContexts", .array([.object([("pushContextId", .string(pushContextID))])])),
        ])]))])]))])
    }

    static func connectCommandBody(consoleDUID: String, accountID: String, sessionID: String, clientType: String,
                                   seeds: ConnectSeeds) -> WireJSON {
        .object([("commandDetail", .object([
            ("platform", .string(CloudEndpoints.currentGenPlatformTag)),
            ("duid", .string(consoleDUID)),
            ("commandType", .string(CloudEndpoints.streamingCommandType)),
            ("parameters", .object([("initialParams", .string(initialParams(accountID: accountID, sessionID: sessionID,
                                                                             clientType: clientType, seeds: seeds)))])),
            ("messageDestination", "SQS"),
        ]))])
    }

    /// `initialParams`, which is itself a JSON *string* inside the command. Fields and order match a
    /// captured vendor command exactly (cap64, re-confirmed against cap96, cap97 and cap107): accountId,
    /// roomId, sessionId, clientType, data1, data2, and nothing else. `data3` is not sent.
    ///
    /// **accountId is a JSON number, not a string**: every capture shows a bare 19-digit integer. It falls
    /// back to a quoted string only if it is somehow not numeric, as .NET does. The comma spacing is the
    /// captures' too.
    static func initialParams(accountID: String, sessionID: String, clientType: String, seeds: ConnectSeeds) -> String {
        let accountJSON = numericAccountID(accountID).map(String.init) ?? WireJSON.quoted(accountID)
        return "{\"accountId\":\(accountJSON), "
            + "\"roomId\":0, "
            + "\"sessionId\":\(WireJSON.quoted(sessionID)), "
            + "\"clientType\":\(WireJSON.quoted(clientType)), "
            + "\"data1\":\(WireJSON.quoted(seeds.data1)), "
            + "\"data2\":\(WireJSON.quoted(seeds.data2))}"
    }

    /// `ulong.TryParse` with its default style: optional surrounding whitespace and an optional leading `+`.
    static func numericAccountID(_ text: String) -> UInt64? {
        var digits = Substring(text.trimmingCharacters(in: .whitespaces))
        if digits.first == "+" { digits = digits.dropFirst() }
        guard !digits.isEmpty, digits.allSatisfy(\.isASCII), digits.allSatisfy(\.isNumber) else { return nil }
        return UInt64(digits)
    }

    /// Field for field against our own capture of one OFFER. `skey` really is sixteen zero bytes at this
    /// stage and `mappedAddr` really is the literal "0.0.0.0": both look like placeholders a reimplementation
    /// would be tempted to fix, and both were verified. `defaultRouteMacAddr` is sent empty rather than
    /// omitted, as the capture shows. `localHashedId` is sent when there is one: the 9303 prelude names both
    /// peers by the ids they published here.
    static func offerBody(accountID: String, candidates: [SignalingCandidate], localHashedID: [UInt8],
                          reqID: Int, sid: Int) -> String {
        WireJSON.object([
            ("action", "OFFER"),
            ("reqId", .int(reqID)),
            ("error", 0),
            ("connRequest", .object([
                ("sid", .int(sid)),
                ("peerSid", 0),
                ("skey", .string(zeroSessionKey)),
                ("natType", 2),
                ("candidate", .array(candidates.map { c -> WireJSON in
                    .object([("type", .string(c.type)), ("addr", .string(c.address)), ("mappedAddr", "0.0.0.0"),
                             ("port", .int(c.port)), ("mappedPort", 0)])
                })),
                ("defaultRouteMacAddr", ""),
                ("localPeerAddr", .object([("accountId", .string(accountID)), ("platform", "REMOTE_PLAY")])),
                ("localHashedId", .string(localHashedID.isEmpty ? "" : Data(localHashedID).base64EncodedString())),
            ])),
        ]).serialized
    }

    static func resultBody(reqID: Int) -> String {
        "{\"action\":\"RESULT\",\"reqId\":\(reqID),\"error\":0,\"connRequest\":{}}"
    }

    /// The ACCEPT, hand-built, as .NET builds it.
    ///
    /// - The candidate describes the **selected pair**, not another advertisement: the *console's* address
    ///   and port in `addr`/`port`, ours in `mappedAddr`/`mappedPort`. The opposite of an OFFER's, and easy
    ///   to get backwards.
    /// - **It is deliberately malformed.** The vendor's client emits `"localPeerAddr":,`, a key with no
    ///   value, and the console accepts it. PSN never parses this (the whole body is an opaque string in the
    ///   payload), so reproducing the bytes is safer than "correcting" them into an untested deviation. Do
    ///   not tidy this. The values are interpolated unescaped, as .NET interpolates them.
    static func acceptBody(reqID: Int, sid: Int, peerSid: Int, consoleCandidate: SignalingCandidate,
                           localAddress: String, localPort: Int) -> String {
        let candidate = "{\"type\":\"\(consoleCandidate.type)\",\"addr\":\"\(consoleCandidate.address)\""
            + ",\"mappedAddr\":\"\(localAddress)\",\"port\":\(consoleCandidate.port),\"mappedPort\":\(localPort)}"
        return "{\"action\":\"ACCEPT\",\"reqId\":\(reqID),\"error\":0,\"connRequest\":{\"sid\":\(sid)"
            + ",\"peerSid\":\(peerSid),\"skey\":\"\(zeroSessionKey)\",\"natType\":0,\"candidate\":[\(candidate)"
            + "],\"defaultRouteMacAddr\":\"\",\"localPeerAddr\":,\"localHashedId\":\"\"}}"
    }

    static func signalingEnvelope(accountID: String, consoleDUID: String, body: String) -> WireJSON {
        .object([
            ("channel", .string(CloudEndpoints.signalingChannel)),
            ("payload", .string("ver=1.0, type=text, body=\(body)")),
            ("to", .array([.object([("accountId", .string(accountID)), ("deviceUniqueId", .string(consoleDUID)),
                                    ("platform", .string(CloudEndpoints.currentGenPlatformTag))])])),
        ])
    }

    /// Sixteen zero bytes, base64: the `skey` our messages carry at this stage.
    static let zeroSessionKey = Data(count: 16).base64EncodedString()

    // MARK: - Transport

    private func getJSON<T: Decodable>(_ type: T.Type, _ url: String) async throws(CloudError) -> T {
        let body = try await send(Self.request("GET", url))
        // `null` or unreadable is one failure, as .NET's "Empty/invalid response" (its JsonException on an
        // unparseable body escapes as the framework's own type; here both are the same case).
        guard let value = (try? JSONDecoder().decode(T?.self, from: body)) ?? nil else {
            throw .invalidResponse("Empty/invalid response from \(url).", body: String(decoding: body, as: UTF8.self))
        }
        return value
    }

    private func sendJSON(_ method: String, _ url: String, _ json: WireJSON) async throws(CloudError) -> Data {
        var request = try Self.request(method, url)
        request.httpBody = Data(json.serialized.utf8)
        request.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type")
        return try await send(request)
    }

    private func send(_ request: URLRequest) async throws(CloudError) -> Data {
        var request = request
        let token = try await tokens.accessToken()
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        // On every captured vendor cloud call. Required on-wire, set per request.
        request.setValue(CloudEndpoints.cloudUserAgent, forHTTPHeaderField: "User-Agent")

        let (status, body) = try await http.send(request)
        guard (200..<300).contains(status) else {
            throw .requestFailed(method: request.httpMethod ?? "GET", url: request.url?.absoluteString ?? "",
                                 status: status, body: String(decoding: body, as: UTF8.self))
        }
        return body
    }

    private static func request(_ method: String, _ url: String) throws(CloudError) -> URLRequest {
        guard let parsed = URL(string: url) else { throw .invalidArgument("not a URL: \(url)") }
        var request = URLRequest(url: parsed)
        request.httpMethod = method
        return request
    }
}
