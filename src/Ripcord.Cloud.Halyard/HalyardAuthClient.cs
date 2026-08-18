using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Ripcord.Cloud.Halyard;

/// <summary>
/// OAuth2 client for account sign-in: the initial authorization-code exchange (after the user signs
/// in via the account web flow / passkey) and the refresh-token grant used on subsequent runs.
/// Structure from docs/protocol/ps5-cloud-session-api.md. The client credential is supplied via
/// <see cref="HalyardClientConfig"/> - never Sony's embedded one.
/// </summary>
public sealed class HalyardAuthClient(HttpClient http, HalyardClientConfig config)
{
    private readonly HttpClient _http = http;
    private readonly HalyardClientConfig _config = config;

    /// <summary>
    /// The URL to open in the account web flow. The user authenticates there (passkey/QR), and the
    /// browser is redirected to <see cref="HalyardClientConfig.RedirectUri"/> with a <c>code</c>
    /// query parameter, which the caller passes to <see cref="ExchangeCodeAsync"/>.
    ///
    /// <para>
    /// <b>No <c>state</c> parameter</b>, which is worth stating because its absence looks like an oversight.
    /// The observed flow sends none, and sending an unrecognised parameter to someone else's authorize endpoint
    /// is a good way to be rejected for no benefit. What <c>state</c> would defend against — an unsolicited code
    /// arriving from somewhere else — does not arise here: the flow runs in a web view this process created for
    /// this one sign-in, and the caller matches the redirect against
    /// <see cref="HalyardClientConfig.RedirectUri"/> before reading anything out of it.
    /// </para>
    /// </summary>
    /// <param name="clientDeviceId">This machine's id, from <see cref="HalyardClientDeviceId"/>.</param>
    public string BuildAuthorizeUrl(string clientDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientDeviceId);

        var q = HttpUtility.ParseQueryString(string.Empty);
        q["service_entity"] = "urn:service-entity:psn";
        q["response_type"] = "code";
        q["client_id"] = _config.ClientId;
        q["redirect_uri"] = _config.RedirectUri;
        q["scope"] = _config.ScopeString;

        // The authorize call carries the same device identity the token exchange does. Omitting these was the
        // difference between a code the token endpoint accepts and one it rejects as issued to another device.
        q["device_type"] = HalyardClientConfig.DeviceType;
        q["duid"] = clientDeviceId;

        // Sign-in page chrome. These do not affect what the code is worth, but they select the compact
        // remote-play styling rather than the full account portal, which in a small web view is the difference
        // between a usable page and one the user has to scroll sideways.
        q["smcid"] = "remoteplay";
        q["ui"] = "pr";
        q["prompt"] = "always";
        q["request_locale"] = CultureInfo.CurrentUICulture.Name.Replace('-', '_');

        return $"{HalyardEndpoints.Authorize}?{q}";
    }

    /// <summary>
    /// Pull the authorization code out of a redirect the web view has landed on, or null when this is not the
    /// redirect we are waiting for.
    ///
    /// <para>
    /// The match is on scheme, host and path only. The account flow bounces through several intermediate URLs
    /// before it arrives, and the final one carries query parameters beyond <c>code</c> — so an equality test
    /// against the configured redirect never fires, and a substring test fires on the wrong page.
    /// </para>
    /// </summary>
    public string? TryReadAuthorizationCode(Uri redirected)
    {
        ArgumentNullException.ThrowIfNull(redirected);

        if (!Uri.TryCreate(_config.RedirectUri, UriKind.Absolute, out Uri? expected))
        {
            return null;
        }

        bool matches =
            string.Equals(redirected.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(redirected.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                redirected.AbsolutePath.TrimEnd('/'),
                expected.AbsolutePath.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);

        if (!matches)
        {
            return null;
        }

        string? code = HttpUtility.ParseQueryString(redirected.Query)["code"];
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    public Task<HalyardTokens> ExchangeCodeAsync(string code, string clientDeviceId, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _config.RedirectUri,
            ["device_type"] = "PC_APP",
            ["duid"] = clientDeviceId,
        };
        return RequestTokenAsync(form, cancellationToken);
    }

    public Task<HalyardTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = _config.ScopeString,
        };
        return RequestTokenAsync(form, cancellationToken);
    }

    private async Task<HalyardTokens> RequestTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        // Checked here rather than at each call site: every path into the token endpoint goes through this
        // method, and an unconfigured build failing with a clear sentence beats a 401 the user cannot act on.
        if (!_config.IsConfigured)
        {
            throw new HalyardCloudException(
                "No OAuth client credential is configured, so account sign-in is unavailable in this build. "
                + "LAN play against an already-paired console does not require one.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, HalyardEndpoints.Token)
        {
            Content = new FormUrlEncodedContent(form),
        };

        string basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_config.ClientId}:{_config.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HalyardCloudException($"Token request failed ({(int)response.StatusCode}).", body);
        }

        HalyardTokenResponse parsed = JsonSerializer.Deserialize<HalyardTokenResponse>(body)
            ?? throw new HalyardCloudException("Token response could not be parsed.", body);

        return new HalyardTokens(
            parsed.AccessToken,
            parsed.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(parsed.ExpiresIn));
    }
}

/// <summary>Raised when a cloud call fails; carries the response body for diagnostics (may hold PII - do not log to shared sinks).</summary>
public sealed class HalyardCloudException(string message, string? responseBody = null) : Exception(message)
{
    public string? ResponseBody { get; } = responseBody;
}
