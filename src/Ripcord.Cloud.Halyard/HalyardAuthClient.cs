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
    /// </summary>
    public string BuildAuthorizeUrl()
    {
        var q = HttpUtility.ParseQueryString(string.Empty);
        q["service_entity"] = "urn:service-entity:psn";
        q["response_type"] = "code";
        q["client_id"] = _config.ClientId;
        q["redirect_uri"] = _config.RedirectUri;
        q["scope"] = _config.ScopeString;
        return $"{HalyardEndpoints.Authorize}?{q}";
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
