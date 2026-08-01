namespace Ripcord.Cloud.Halyard;

/// <summary>
/// The OAuth client identity Ripcord authenticates as. Deliberately NOT hardcoded to the official
/// app's embedded credential (clean-room + it isn't ours to embed) - it is supplied at runtime.
/// The scopes and redirect URI mirror what the connect flow requires
/// (docs/protocol/ps5-cloud-session-api.md).
/// </summary>
public sealed record HalyardClientConfig(
    string ClientId,
    string ClientSecret,
    string RedirectUri,
    IReadOnlyList<string> Scopes)
{
    /// <summary>Scopes observed on the official flow; scope strings are required on-wire tokens.</summary>
    public static IReadOnlyList<string> DefaultScopes { get; } =
    [
        "psn:clientapp",
        "referenceDataService:countryConfig.read",
        "pushNotification:webSocket.desktop.connect",
        "sessionManager:remotePlaySession.system.update",
        "sbahn:pc.telemetry.publish",
    ];

    public string ScopeString => string.Join(' ', Scopes);
}

/// <summary>Tokens for the signed-in account. The refresh token is the long-lived credential.</summary>
public sealed record HalyardTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromMinutes(1);
}
