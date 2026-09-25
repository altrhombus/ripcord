// The console's signaling, recovered from push frames, and the other two push notifications the connect
// flow acts on. Ported from HalyardSignalingMessage.cs and HalyardCustomDataNotification.cs.
//
// A FORGIVING READER, NOT Codable. The frame nests a JSON document inside a string field
// (`payload = "ver=1.0, type=text, body={...}"`), the shape varies by action, unknown fields must be
// tolerated, and most frames are not signaling at all. .NET uses JsonDocument for the same reasons; the
// Swift counterpart is JSONSerialization over [String: Any]. Where .NET would throw on a field of the wrong
// JSON type (its GetString and TryGetInt32 do, outside the JsonException they catch), this treats the field
// as absent instead: a malformed frame parses to "not signaling" either way, but here it cannot escape as
// an exception from the push loop.

import Foundation

public struct SignalingMessage: Sendable, Equatable {
    /// `OFFER`, `ACCEPT` or `RESULT`.
    public var action: String
    public var reqID: Int
    /// `PROSPERO` (PS5), a PS4 tag, or `REMOTE_PLAY`: our own message, echoed back on the same channel.
    public var fromPlatform: String?
    public var candidates: [SignalingCandidate]
    /// The `skey`: 16 bytes when the peer has filled it in; nil when absent.
    public var sessionKey: [UInt8]?
    /// The `localHashedId`: 20 bytes in the console's OFFER; nil when absent.
    public var localHashedID: [UInt8]?
    /// The peer's own stream id for this connection. Our ACCEPT names it as `peerSid`.
    public var sid: Int
    /// The id the peer believes is ours. Zero in an OFFER.
    public var peerSid: Int

    public init(action: String, reqID: Int, fromPlatform: String?, candidates: [SignalingCandidate],
                sessionKey: [UInt8]? = nil, localHashedID: [UInt8]? = nil, sid: Int = 0, peerSid: Int = 0) {
        self.action = action
        self.reqID = reqID
        self.fromPlatform = fromPlatform
        self.candidates = candidates
        self.sessionKey = sessionKey
        self.localHashedID = localHashedID
        self.sid = sid
        self.peerSid = peerSid
    }

    static let sessionMessageDataType = "sessionMessage:created"
    static let ownPlatform = "REMOTE_PLAY"

    /// The console's OFFER: the message whose candidates are worth acting on.
    public var isConsoleOffer: Bool {
        action == "OFFER" && !candidates.isEmpty && fromPlatform != Self.ownPlatform
    }

    /// The peer sent this and expects a RESULT carrying its reqId.
    ///
    /// The ACCEPT counts as well as the OFFER: five captures of the vendor client (ps-rendezvous cap71,
    /// cap72, cap96, cap99, cap107) show it acknowledging the console's ACCEPT every time, and a Ripcord that
    /// acknowledged only the OFFER watched the console send its ACCEPT, wait, and TERMINATE in every live
    /// run. A RESULT is not acknowledged: nothing in any capture acks an ack.
    public var expectsResult: Bool {
        (action == "OFFER" || action == "ACCEPT") && fromPlatform != Self.ownPlatform
    }

    /// One push frame as a signaling message, or nil when it is not one: a presence, members or customData
    /// notification, a message on another channel, or malformed. Nil is the common case, not an error.
    public static func parse(pushFrame: String) -> SignalingMessage? {
        guard let root = PushJSON.object(pushFrame),
              PushJSON.string(root["dataType"])?.hasSuffix(sessionMessageDataType) == true,
              let body = root["body"] as? [String: Any],
              let data = body["data"] as? [String: Any],
              let sessionMessage = data["sessionMessage"] as? [String: Any],
              PushJSON.string(sessionMessage["channel"]) == CloudEndpoints.signalingChannel,
              let inner = extractBody(PushJSON.string(sessionMessage["payload"])) else { return nil }

        let from = ((data["customProperties"] as? [String: Any])?["from"] as? [String: Any])?["platform"]
        return parseConnRequest(inner, fromPlatform: PushJSON.string(from))
    }

    /// The JSON after the payload's first `body=`. Everything after it is the document, which may itself
    /// contain `=`, so splitting on `=` would truncate it.
    static func extractBody(_ payload: String?) -> String? {
        guard let payload, let marker = payload.range(of: "body=") else { return nil }
        return String(payload[marker.upperBound...].drop(while: \.isWhitespace))
    }

    /// The vendor writes `"localPeerAddr":,` (a key with no value) in an ACCEPT, which is not JSON. Repaired
    /// to `null` before parsing.
    ///
    /// This was a real, silent bug for the whole life of the .NET account route: the parse threw, the frame
    /// was dropped as "not signaling", the console's ACCEPT was never acknowledged, and every live pairing
    /// ended in its TERMINATE. Ripcord sends this exact malformation itself, so it must read it back.
    static func repairVendorJSON(_ body: String) -> String {
        body.replacing(#/"localPeerAddr"\s*:\s*,/#) { _ in "\"localPeerAddr\":null," }
    }

    static func parseConnRequest(_ inner: String, fromPlatform: String?) -> SignalingMessage? {
        guard let root = PushJSON.object(repairVendorJSON(inner)), root["action"] != nil else { return nil }

        var message = SignalingMessage(action: PushJSON.string(root["action"]) ?? "",
                                       reqID: PushJSON.int32(root["reqId"]) ?? 0,
                                       fromPlatform: fromPlatform, candidates: [])

        if let conn = root["connRequest"] as? [String: Any] {
            message.sessionKey = PushJSON.base64(conn["skey"])
            message.localHashedID = PushJSON.base64(conn["localHashedId"])
            message.sid = PushJSON.int32(conn["sid"]) ?? 0
            message.peerSid = PushJSON.int32(conn["peerSid"]) ?? 0

            for case let c as [String: Any] in (conn["candidate"] as? [Any]) ?? [] {
                guard let type = PushJSON.string(c["type"]), !type.isEmpty,
                      let address = PushJSON.string(c["addr"]), !address.isEmpty else { continue }
                message.candidates.append(SignalingCandidate(type: type, address: address,
                                                             port: PushJSON.int32(c["port"]) ?? 0))
            }
        }
        return message
    }
}

/// The two other notifications the connect flow reads off the push channel.
public enum CustomDataNotification {
    static let customData1DataType = "rps:customData1:updated"
    static let memberJoinedDataType = "rps:members:created"
    /// The platform tag a PS5 joins under. A required on-wire value.
    static let consolePlatform = "PROSPERO"

    /// The raw (double-base64) `customData1` from a frame, or nil when the frame is not one. It carries the
    /// account-pairing seed, field-encrypted with the connect command's data1/data2; decrypting it is the C
    /// core's job (halyard_account_seed_recover_custom_data1), since this layer holds no crypto.
    public static func customData1(inPushFrame frame: String) -> String? {
        guard let root = PushJSON.object(frame),
              PushJSON.string(root["dataType"])?.hasSuffix(customData1DataType) == true,
              let data = (root["body"] as? [String: Any])?["data"] as? [String: Any],
              let value = PushJSON.string(data["customData1"]), !value.isEmpty else { return nil }
        return value
    }

    /// Whether this frame says the *console* has joined the session: the first moment a client may message
    /// it. Our own join arrives here too, tagged `REMOTE_PLAY`, and must not be mistaken for it.
    public static func isConsoleJoined(pushFrame frame: String) -> Bool {
        guard let root = PushJSON.object(frame),
              PushJSON.string(root["dataType"])?.hasSuffix(memberJoinedDataType) == true,
              let data = (root["body"] as? [String: Any])?["data"] as? [String: Any],
              let members = data["members"] as? [Any] else { return false }
        return members.contains { PushJSON.string(($0 as? [String: Any])?["platform"]) == consolePlatform }
    }
}

/// JSONSerialization, read the way JsonDocument's accessors read.
enum PushJSON {
    static func object(_ text: String) -> [String: Any]? {
        guard !text.isBlank, let data = text.data(using: .utf8) else { return nil }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    static func string(_ value: Any?) -> String? { value as? String }

    /// `TryGetInt32`: an integral JSON number that fits 32 bits, and nothing else (not a bool, not a string).
    static func int32(_ value: Any?) -> Int? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let double = number.doubleValue
        guard double.rounded() == double, double >= Double(Int32.min), double <= Double(Int32.max) else { return nil }
        return number.intValue
    }

    /// `Convert.FromBase64String` over a non-empty string, or nil.
    static func base64(_ value: Any?) -> [UInt8]? {
        guard let text = value as? String, !text.isEmpty, let data = Data(base64Encoded: text) else { return nil }
        return Array(data)
    }
}
