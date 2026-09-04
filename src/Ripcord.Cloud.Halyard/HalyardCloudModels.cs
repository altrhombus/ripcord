using System.Text.Json.Serialization;

namespace Ripcord.Cloud.Halyard;

/// <summary>
/// Cloud endpoints and on-wire tokens, from docs/protocol/ps5-cloud-session-api.md. Hostnames and
/// path/tag literals here are the real service addresses/values the API requires - not our naming.
/// </summary>
public static class HalyardEndpoints
{
    public const string Token = "https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/token";
    public const string Authorize = "https://ca.account.sony.com/api/v1/oauth/authorize";
    public const string AccountInfo = "https://vl.api.np.km.playstation.net/vl/api/v1/s2s/users/me/info";
    public const string ConsoleList = "https://web.np.playstation.com/api/cloudAssistedNavigation/v2/users/me/clients";
    public const string Commands = "https://web.np.playstation.com/api/cloudAssistedNavigation/v2/users/me/commands";
    public const string Sessions = "https://web.np.playstation.com/api/sessionManager/v1/remotePlaySessions";

    /// <summary>
    /// The push front-end <b>lookup</b>. Returns the specific push host to open the persistent WebSocket to —
    /// the connection itself goes to that returned host, not here. Query values are required on-wire.
    /// </summary>
    public const string PushServerAddress =
        "https://mobile-pushcl.np.communication.playstation.net/np/serveraddr?version=2.1&fields=keepAliveStatus&keepAliveStatusType=3";

    /// <summary>The path the push WebSocket upgrades on, appended to the host from the lookup above.</summary>
    public const string PushNotificationPath = "/np/pushNotification";

    /// <summary>
    /// Required header on a session readback: the session id(s) to return. A <c>GET remotePlaySessions</c>
    /// without it is a 400 (wire-confirmed). Comma-separated for several; we read one at a time.
    /// </summary>
    public const string SessionIdsHeader = "X-PSN-SESSION-MANAGER-SESSION-IDS";

    /// <summary>Required platform-tag wire value for the current-gen console in the cloud API.</summary>
    public const string CurrentGenPlatformTag = "PS5";

    /// <summary>Required command-type wire value that triggers a streaming session on the console.</summary>
    public const string StreamingCommandType = "remotePlay";

    /// <summary>Required signaling channel wire value.</summary>
    public const string SignalingChannel = "remote_play:1";

    /// <summary>
    /// The <c>User-Agent</c> the vendor client sends on every cloud REST call (captured, cap107 and every
    /// other rendezvous capture). A required on-wire value, not a naming choice — see the naming rules in
    /// CLAUDE.md, which keep interoperability facts verbatim.
    /// </summary>
    public const string CloudUserAgent = "RpNetHttpUtilImpl";
}

// ---- OAuth ----

public sealed record HalyardTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

// ---- Account ----

public sealed record HalyardAccountInfo(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("onlineId")] string OnlineId,
    [property: JsonPropertyName("region")] string Region);

// ---- Console list ----

public sealed record HalyardDevice(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("wakeupEnabledPowerModes")] string[]? WakeupEnabledPowerModes,
    [property: JsonPropertyName("enabledFeatures")] string[]? EnabledFeatures);

public sealed record HalyardConsoleClient(
    [property: JsonPropertyName("device")] HalyardDevice Device,
    [property: JsonPropertyName("duid")] string Duid,
    [property: JsonPropertyName("platform")] string Platform)
{
    public bool RemotePlayEnabled => HasFeature("remotePlay");
    public bool CanWake => Device.WakeupEnabledPowerModes is { Length: > 0 };

    private bool HasFeature(string feature) =>
        Device.EnabledFeatures is { } f && Array.Exists(f, x => string.Equals(x, feature, StringComparison.OrdinalIgnoreCase));
}

public sealed record HalyardConsoleListResponse(
    [property: JsonPropertyName("clients")] HalyardConsoleClient[] Clients);

// ---- Session manager ----

public sealed record HalyardSessionMember(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("deviceUniqueId")] string? DeviceUniqueId);

public sealed record HalyardCloudSession(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("members")] HalyardSessionMember[]? Members);

public sealed record HalyardSessionsResponse(
    [property: JsonPropertyName("remotePlaySessions")] HalyardCloudSession[] RemotePlaySessions);

// ---- Commands ----

public sealed record HalyardCommandResponse(
    [property: JsonPropertyName("commandId")] string CommandId);

// ---- Push channel ----

/// <summary>Keepalive timing the push service dictates, in milliseconds.</summary>
public sealed record HalyardPushKeepAlive(
    [property: JsonPropertyName("clientKeepAliveInterval")] int ClientKeepAliveIntervalMs,
    [property: JsonPropertyName("clientKeepAliveTimeout")] int ClientKeepAliveTimeoutMs,
    [property: JsonPropertyName("serverKeepAliveTimeout")] int ServerKeepAliveTimeoutMs,
    [property: JsonPropertyName("serverPresenceTimeout")] int ServerPresenceTimeoutMs);

/// <summary>
/// The push front-end the client should connect to, from the serveraddr lookup. The persistent WebSocket opens
/// against <see cref="Fqdn"/>, not the lookup host, and pings on <see cref="HalyardPushKeepAlive"/>'s cadence.
/// </summary>
public sealed record HalyardPushServerInfo(
    [property: JsonPropertyName("fqdn")] string Fqdn,
    [property: JsonPropertyName("keepAliveStatus")] HalyardPushKeepAlive? KeepAlive)
{
    /// <summary>The full <c>wss://</c> URI to upgrade the push WebSocket on.</summary>
    public Uri PushUri => new($"wss://{Fqdn}{HalyardEndpoints.PushNotificationPath}");

    /// <summary>How often the client should ping. Falls back to 10 s — the observed default — if unspecified.</summary>
    public TimeSpan ClientKeepAlive => TimeSpan.FromMilliseconds(
        KeepAlive is { ClientKeepAliveIntervalMs: > 0 } ? KeepAlive.ClientKeepAliveIntervalMs : 10_000);
}
