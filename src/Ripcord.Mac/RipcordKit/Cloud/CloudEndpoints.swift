// Cloud endpoints and on-wire tokens, ported from src/Ripcord.Cloud.Halyard/HalyardCloudModels.cs
// (HalyardEndpoints) and HalyardPushChannel.cs (HalyardPushHeaders). Structure from
// docs/protocol/ps5-cloud-session-api.md.
//
// Every string here is an interoperability fact, not a naming choice (CLAUDE.md, "Naming"): hostnames,
// paths, header names and values, platform tags. They are verbatim from the .NET reference, which has them
// verbatim from our own captures. Do not tidy them, and do not "fix" the Windows ones for the Mac: they are
// what the service has been shown to accept, and nothing has shown it accepts anything else.

import Foundation

public enum CloudEndpoints {
    public static let token = "https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/token"
    public static let authorize = "https://ca.account.sony.com/api/v1/oauth/authorize"
    public static let accountInfo = "https://vl.api.np.km.playstation.net/vl/api/v1/s2s/users/me/info"
    public static let consoleList = "https://web.np.playstation.com/api/cloudAssistedNavigation/v2/users/me/clients"
    public static let commands = "https://web.np.playstation.com/api/cloudAssistedNavigation/v2/users/me/commands"
    public static let sessions = "https://web.np.playstation.com/api/sessionManager/v1/remotePlaySessions"

    /// The push front-end *lookup*. It returns the host to open the WebSocket to; the connection itself goes
    /// there, not here. The query values are required on-wire.
    public static let pushServerAddress =
        "https://mobile-pushcl.np.communication.playstation.net/np/serveraddr?version=2.1&fields=keepAliveStatus&keepAliveStatusType=3"

    /// The path the push WebSocket upgrades on, appended to the host from the lookup.
    public static let pushNotificationPath = "/np/pushNotification"

    /// Required on a session readback: a `GET remotePlaySessions` without it is a 400 (wire-confirmed).
    public static let sessionIDsHeader = "X-PSN-SESSION-MANAGER-SESSION-IDS"

    /// The platform tag for the current-generation console in the cloud API.
    public static let currentGenPlatformTag = "PS5"

    /// The command type that triggers a streaming session on the console.
    public static let streamingCommandType = "remotePlay"

    /// The signaling channel.
    public static let signalingChannel = "remote_play:1"

    /// The `User-Agent` the vendor client sends on every cloud REST call (captured, cap107 and every other
    /// rendezvous capture).
    public static let cloudUserAgent = "RpNetHttpUtilImpl"
}

/// The push WebSocket upgrade's required values, captured verbatim from a vendor handshake (cap68). The
/// front-end 400s an upgrade without the subprotocol and expects the `X-PSN-*` set.
///
/// **They say Windows, and stay saying Windows.** `OsVersion` is the captured value. Whether the service
/// would accept a Mac-shaped one is [X]: nothing has tested it, and the Windows client has shown this one
/// works. The same goes for the `Windows` client type the connect command carries.
public enum PushHeaders {
    /// The subprotocol the push front-end requires; omitting it is a 400.
    public static let subProtocol = "np-pushpacket"
    public static let userAgent = "WebSocket++/0.8.2"
    public static let appType = "REMOTE_PLAY"
    public static let appVersion = "RemotePlay/1.0"
    public static let osVersion = "Windows/10.0"
    public static let protocolVersion = "2.1"
    public static let keepAliveStatusType = "3"
    /// False for the initial connect; a reconnect after a drop would set this true.
    public static let reconnection = "false"
}

/// Tunables the .NET reference fixes, collected so the port's timing can be checked against it in one place.
public enum CloudTiming {
    /// HalyardAppServices builds the account gateway's HttpClient with a 20-second timeout rather than the
    /// default hundred: every call is a short request the user is waiting on, and the connect path retries.
    public static let requestTimeout: TimeInterval = 20
    /// HalyardWanConnection's leave-on-disconnect.
    public static let wanLeaveTimeout: Duration = .seconds(3)
    /// HalyardAccountPairing.LeaveQuietlyAsync.
    public static let accountLeaveTimeout: Duration = .seconds(10)
    /// ClientWebSocketChannel's best-effort clean close.
    public static let webSocketCloseTimeout: Duration = .seconds(2)
    /// HalyardPushServerInfo.ClientKeepAlive's fallback when the lookup names none.
    public static let defaultKeepAlive: Duration = .seconds(10)
}
