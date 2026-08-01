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

    /// <summary>Required platform-tag wire value for the current-gen console in the cloud API.</summary>
    public const string CurrentGenPlatformTag = "PS5";

    /// <summary>Required command-type wire value that triggers a streaming session on the console.</summary>
    public const string StreamingCommandType = "remotePlay";

    /// <summary>Required signaling channel wire value.</summary>
    public const string SignalingChannel = "remote_play:1";
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
