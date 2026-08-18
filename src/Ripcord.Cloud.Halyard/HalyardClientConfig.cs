namespace Ripcord.Cloud.Halyard;

/// <summary>
/// The OAuth client identity Ripcord authenticates as.
///
/// <para>
/// <b>Resolved at runtime, never compiled in.</b> The value itself is bundled as data
/// (<see cref="HalyardBundledClient"/>) and read through <see cref="HalyardClientConfigFile"/>, which prefers
/// the environment and a user's own <c>client.json</c> over it. So this type stays a plain carrier: nothing
/// here knows or cares where the credential came from, which is what lets a build ship without one and lets an
/// operator substitute their own.
/// </para>
///
/// <para>
/// <see cref="IsConfigured"/> is how every surface asks whether signing in is even possible, and it is asked
/// rather than assumed because it is genuinely false in two ordinary cases: a
/// <c>-p:BundleOAuthClient=false</c> build, and a checkout without the dirty room that has never populated the
/// placeholder. Either way the app is fully functional for LAN play against a paired console; it simply cannot
/// reach the account tier. See <c>NOTICE</c> for what is bundled and why.
/// </para>
/// </summary>
public sealed record HalyardClientConfig(
    string ClientId,
    string ClientSecret,
    string RedirectUri,
    IReadOnlyList<string> Scopes)
{
    /// <summary>
    /// The redirect the account web flow sends the authorization code to. A required on-wire value — the client
    /// credential is registered against this exact URL, so it is not ours to choose — and a public one: it is
    /// recorded in <c>docs/protocol/ps5-cloud-session-api.md</c> and appears in the redirect the browser
    /// follows. Nothing is served from it that we use; the code is read out of the URL.
    /// </summary>
    public const string DefaultRedirectUri = "https://remoteplay.dl.playstation.net/remoteplay/redirect";

    /// <summary>Required wire value identifying this client as a desktop app in the token and authorize calls.</summary>
    public const string DeviceType = "PC_APP";

    /// <summary>Scopes observed on the official flow; scope strings are required on-wire tokens.</summary>
    public static IReadOnlyList<string> DefaultScopes { get; } =
    [
        "psn:clientapp",
        "referenceDataService:countryConfig.read",
        "pushNotification:webSocket.desktop.connect",
        "sessionManager:remotePlaySession.system.update",
        "sbahn:pc.telemetry.publish",
    ];

    /// <summary>
    /// An explicitly empty client: no credential, standard redirect and scopes. What
    /// <see cref="IsConfigured"/> reports false for, and what a build that omitted the bundle resolves to.
    /// </summary>
    public static HalyardClientConfig Unconfigured { get; } =
        new(string.Empty, string.Empty, DefaultRedirectUri, DefaultScopes);

    /// <summary>
    /// Whether this configuration can actually be used to sign in. Checked before any surface offers a sign-in
    /// affordance, so the failure is "sign-in is not available in this build", stated up front, rather than an
    /// HTTP 401 after the user has typed their password into a web view.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RedirectUri);

    public string ScopeString => string.Join(' ', Scopes);

    /// <summary>
    /// Read a configuration from the environment, for the console harness and for a developer build that has been
    /// given a credential out of band. Returns <see cref="Unconfigured"/> shape when the variables are absent,
    /// which <see cref="IsConfigured"/> then reports on.
    /// </summary>
    public static HalyardClientConfig FromEnvironment() => new(
        ClientId: Environment.GetEnvironmentVariable("RIPCORD_CLIENT_ID") ?? string.Empty,
        ClientSecret: Environment.GetEnvironmentVariable("RIPCORD_CLIENT_SECRET") ?? string.Empty,
        RedirectUri: Environment.GetEnvironmentVariable("RIPCORD_REDIRECT_URI") ?? DefaultRedirectUri,
        Scopes: DefaultScopes);
}

/// <summary>Tokens for the signed-in account. The refresh token is the long-lived credential.</summary>
public sealed record HalyardTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromMinutes(1);
}
