// The cloud's response shapes, ported from HalyardCloudModels.cs. Codable, with the JSON keys the .NET
// JsonPropertyName attributes name.
//
// DECODING IS FORGIVING, deliberately and to match. System.Text.Json fills a missing string property of a
// positional record with null and a missing number with zero rather than failing, and the .NET callers
// then use `?? []` and friends. A Swift Codable property that is not optional would instead reject the
// whole response over one absent field, which is a behaviour change nobody tested. So every field decodes
// "if present", with the default .NET would have produced.

import Foundation

public struct TokenResponse: Sendable, Equatable, Decodable {
    public var accessToken: String
    public var refreshToken: String
    public var tokenType: String
    public var expiresIn: Int
    public var scope: String

    enum CodingKeys: String, CodingKey {
        case accessToken = "access_token", refreshToken = "refresh_token", tokenType = "token_type"
        case expiresIn = "expires_in", scope
    }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        accessToken = try c.decodeIfPresent(String.self, forKey: .accessToken) ?? ""
        refreshToken = try c.decodeIfPresent(String.self, forKey: .refreshToken) ?? ""
        tokenType = try c.decodeIfPresent(String.self, forKey: .tokenType) ?? ""
        expiresIn = try c.decodeIfPresent(Int.self, forKey: .expiresIn) ?? 0
        scope = try c.decodeIfPresent(String.self, forKey: .scope) ?? ""
    }
}

/// The signed-in account's tokens. The refresh token is the long-lived credential.
public struct AccountTokens: Sendable, Equatable {
    public var accessToken: String
    public var refreshToken: String
    public var expiresAt: Date

    public init(accessToken: String, refreshToken: String, expiresAt: Date) {
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.expiresAt = expiresAt
    }

    /// A minute early, as .NET, so a token does not expire between the check and the call.
    public func isExpired(at now: Date = Date()) -> Bool { now >= expiresAt.addingTimeInterval(-60) }
}

public struct AccountInfo: Sendable, Equatable, Decodable {
    /// Decimal, and already the form the console pairing exchange parses.
    public var accountID: String
    public var onlineID: String
    public var region: String

    enum CodingKeys: String, CodingKey { case accountID = "accountId", onlineID = "onlineId", region }

    public init(accountID: String, onlineID: String, region: String) {
        self.accountID = accountID
        self.onlineID = onlineID
        self.region = region
    }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        accountID = try c.decodeIfPresent(String.self, forKey: .accountID) ?? ""
        onlineID = try c.decodeIfPresent(String.self, forKey: .onlineID) ?? ""
        region = try c.decodeIfPresent(String.self, forKey: .region) ?? ""
    }
}

public struct ConsoleDevice: Sendable, Equatable, Decodable {
    public var name: String
    public var wakeupEnabledPowerModes: [String]?
    public var enabledFeatures: [String]?

    public init(name: String, wakeupEnabledPowerModes: [String]? = nil, enabledFeatures: [String]? = nil) {
        self.name = name
        self.wakeupEnabledPowerModes = wakeupEnabledPowerModes
        self.enabledFeatures = enabledFeatures
    }

    enum CodingKeys: String, CodingKey { case name, wakeupEnabledPowerModes, enabledFeatures }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
        wakeupEnabledPowerModes = try c.decodeIfPresent([String].self, forKey: .wakeupEnabledPowerModes)
        enabledFeatures = try c.decodeIfPresent([String].self, forKey: .enabledFeatures)
    }
}

/// One console on the account, from the console list.
public struct CloudConsole: Sendable, Equatable, Decodable {
    public var device: ConsoleDevice
    public var duid: String
    public var platform: String

    public init(device: ConsoleDevice, duid: String, platform: String) {
        self.device = device
        self.duid = duid
        self.platform = platform
    }

    enum CodingKeys: String, CodingKey { case device, duid, platform }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        device = try c.decodeIfPresent(ConsoleDevice.self, forKey: .device) ?? ConsoleDevice(name: "")
        duid = try c.decodeIfPresent(String.self, forKey: .duid) ?? ""
        platform = try c.decodeIfPresent(String.self, forKey: .platform) ?? ""
    }

    public var remotePlayEnabled: Bool {
        device.enabledFeatures?.contains { $0.caseInsensitiveCompare("remotePlay") == .orderedSame } ?? false
    }

    /// Whether the account service says it can be woken. Not whether it is awake: that is asked on connect.
    public var canWake: Bool { !(device.wakeupEnabledPowerModes ?? []).isEmpty }
}

struct ConsoleListResponse: Decodable {
    var clients: [CloudConsole]?
}

public struct SessionMember: Sendable, Equatable, Decodable {
    public var accountID: String
    public var platform: String
    public var deviceUniqueID: String?

    public init(accountID: String, platform: String, deviceUniqueID: String? = nil) {
        self.accountID = accountID
        self.platform = platform
        self.deviceUniqueID = deviceUniqueID
    }

    enum CodingKeys: String, CodingKey { case accountID = "accountId", platform, deviceUniqueID = "deviceUniqueId" }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        accountID = try c.decodeIfPresent(String.self, forKey: .accountID) ?? ""
        platform = try c.decodeIfPresent(String.self, forKey: .platform) ?? ""
        deviceUniqueID = try c.decodeIfPresent(String.self, forKey: .deviceUniqueID)
    }
}

public struct CloudSession: Sendable, Equatable, Decodable {
    public var sessionID: String
    public var members: [SessionMember]?

    public init(sessionID: String, members: [SessionMember]? = nil) {
        self.sessionID = sessionID
        self.members = members
    }

    enum CodingKeys: String, CodingKey { case sessionID = "sessionId", members }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        sessionID = try c.decodeIfPresent(String.self, forKey: .sessionID) ?? ""
        members = try c.decodeIfPresent([SessionMember].self, forKey: .members)
    }
}

struct SessionsResponse: Decodable {
    var remotePlaySessions: [CloudSession]?
}

struct CommandResponse: Decodable {
    var commandId: String?
}

/// The keepalive timing the push service dictates, in milliseconds.
public struct PushKeepAlive: Sendable, Equatable, Decodable {
    public var clientKeepAliveIntervalMs: Int
    public var clientKeepAliveTimeoutMs: Int
    public var serverKeepAliveTimeoutMs: Int
    public var serverPresenceTimeoutMs: Int

    public init(clientKeepAliveIntervalMs: Int, clientKeepAliveTimeoutMs: Int, serverKeepAliveTimeoutMs: Int,
                serverPresenceTimeoutMs: Int) {
        self.clientKeepAliveIntervalMs = clientKeepAliveIntervalMs
        self.clientKeepAliveTimeoutMs = clientKeepAliveTimeoutMs
        self.serverKeepAliveTimeoutMs = serverKeepAliveTimeoutMs
        self.serverPresenceTimeoutMs = serverPresenceTimeoutMs
    }

    enum CodingKeys: String, CodingKey {
        case clientKeepAliveIntervalMs = "clientKeepAliveInterval", clientKeepAliveTimeoutMs = "clientKeepAliveTimeout"
        case serverKeepAliveTimeoutMs = "serverKeepAliveTimeout", serverPresenceTimeoutMs = "serverPresenceTimeout"
    }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        clientKeepAliveIntervalMs = try c.decodeIfPresent(Int.self, forKey: .clientKeepAliveIntervalMs) ?? 0
        clientKeepAliveTimeoutMs = try c.decodeIfPresent(Int.self, forKey: .clientKeepAliveTimeoutMs) ?? 0
        serverKeepAliveTimeoutMs = try c.decodeIfPresent(Int.self, forKey: .serverKeepAliveTimeoutMs) ?? 0
        serverPresenceTimeoutMs = try c.decodeIfPresent(Int.self, forKey: .serverPresenceTimeoutMs) ?? 0
    }
}

/// The push front-end to connect to, from the serveraddr lookup. The WebSocket opens against `fqdn`, not the
/// lookup host.
public struct PushServerInfo: Sendable, Equatable, Decodable {
    public var fqdn: String
    public var keepAlive: PushKeepAlive?

    public init(fqdn: String, keepAlive: PushKeepAlive? = nil) {
        self.fqdn = fqdn
        self.keepAlive = keepAlive
    }

    enum CodingKeys: String, CodingKey { case fqdn, keepAlive = "keepAliveStatus" }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        fqdn = try c.decodeIfPresent(String.self, forKey: .fqdn) ?? ""
        keepAlive = try c.decodeIfPresent(PushKeepAlive.self, forKey: .keepAlive)
    }

    /// The `wss://` URL to upgrade on.
    public var pushURL: URL? { URL(string: "wss://\(fqdn)\(CloudEndpoints.pushNotificationPath)") }

    /// How often to ping: the service's figure, or 10 s (the observed default) when it names none.
    public var clientKeepAlive: Duration {
        if let ms = keepAlive?.clientKeepAliveIntervalMs, ms > 0 { return .milliseconds(ms) }
        return CloudTiming.defaultKeepAlive
    }
}

/// Who is signed in, as far as the cloud tier is concerned.
public struct CloudAccount: Sendable, Equatable {
    public var accountID: String
    public var onlineID: String
    public var region: String

    public init(accountID: String, onlineID: String, region: String) {
        self.accountID = accountID
        self.onlineID = onlineID
        self.region = region
    }
}

/// A candidate: where a peer can be reached. Kept as the wire string for the type, not an enum, so a kind we
/// have not seen round-trips and is ignored rather than failing the parse. One type serves what .NET splits
/// into HalyardCandidate (ours, outbound) and HalyardSignalingCandidate (theirs, inbound); they have the same
/// three fields.
public struct SignalingCandidate: Sendable, Equatable, Hashable {
    /// `LOCAL`, `STATIC` or `STUN`.
    public var type: String
    public var address: String
    public var port: Int

    public init(type: String, address: String, port: Int) {
        self.type = type
        self.address = address
        self.port = port
    }
}

/// The three 16-byte values the connect command carries, base64. Only the first two are sent; the third is
/// kept because the .NET signature has it, and its role is [X].
public struct ConnectSeeds: Sendable, Equatable {
    public var data1: String
    public var data2: String
    public var data3: String

    public init(data1: String, data2: String, data3: String = "") {
        self.data1 = data1
        self.data2 = data2
        self.data3 = data3
    }

    /// Fresh random values of the right shape, for a connect that does not need to decrypt anything.
    public static func random() -> ConnectSeeds {
        func seed() -> String {
            var bytes = [UInt8](repeating: 0, count: 16)
            for i in bytes.indices { bytes[i] = UInt8.random(in: .min ... .max) }
            return Data(bytes).base64EncodedString()
        }
        return ConnectSeeds(data1: seed(), data2: seed(), data3: seed())
    }
}
