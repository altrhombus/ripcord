using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Ripcord.Cloud.Halyard;

/// <summary>An ICE-style candidate advertised in a signaling OFFER (docs/protocol/ps5-cloud-session-api.md).</summary>
public sealed record HalyardCandidate(string Type, string Address, int Port);

/// <summary>
/// The authenticated cloud REST surface for the connect flow: account info, the console list, the
/// session-manager lifecycle (create/read/leave), the wake/trigger command, and the candidate
/// signaling channel. Structure and ordering from docs/protocol/ps5-cloud-session-api.md.
/// </summary>
public sealed class HalyardCloudClient(HttpClient http, HalyardTokenProvider tokens)
{
    private readonly HttpClient _http = http;
    private readonly HalyardTokenProvider _tokens = tokens;

    public async Task<HalyardAccountInfo> GetAccountInfoAsync(CancellationToken cancellationToken)
        => await GetJsonAsync<HalyardAccountInfo>(HalyardEndpoints.AccountInfo, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<HalyardConsoleClient>> ListConsolesAsync(CancellationToken cancellationToken)
    {
        string url = $"{HalyardEndpoints.ConsoleList}?platform={HalyardEndpoints.CurrentGenPlatformTag}&includeFields=device&limit=10&offset=0";
        var response = await GetJsonAsync<HalyardConsoleListResponse>(url, cancellationToken).ConfigureAwait(false);
        return response.Clients ?? [];
    }

    /// <summary>Create/join an account-scoped session; the response carries the session id.</summary>
    public async Task<HalyardCloudSession> CreateSessionAsync(string pushContextId, CancellationToken cancellationToken)
    {
        var body = new
        {
            remotePlaySessions = new[]
            {
                new
                {
                    members = new[]
                    {
                        new
                        {
                            accountId = "me",
                            deviceUniqueId = "me",
                            platform = "me",
                            pushContexts = new[] { new { pushContextId } },
                        },
                    },
                },
            },
        };

        var response = await SendJsonAsync<HalyardSessionsResponse>(
            HttpMethod.Post, HalyardEndpoints.Sessions, body, cancellationToken).ConfigureAwait(false)
            ?? throw new HalyardCloudException("Session create returned no session.");
        return response.RemotePlaySessions[0];
    }

    public async Task<IReadOnlyList<HalyardCloudSession>> GetSessionsAsync(CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<HalyardSessionsResponse>(HalyardEndpoints.Sessions, cancellationToken).ConfigureAwait(false);
        return response.RemotePlaySessions ?? [];
    }

    public async Task LeaveSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        string url = $"{HalyardEndpoints.Sessions}/{sessionId}/members/me";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Send the wake/trigger command to the console (targeted by duid) that starts a streaming
    /// session. The seed bytes are handed to the console to bootstrap the direct session's crypto.
    /// </summary>
    public async Task<string> SendConnectCommandAsync(
        string consoleDuid,
        string accountId,
        string sessionId,
        string clientType,
        (string Data1, string Data2, string Data3) seeds,
        CancellationToken cancellationToken)
    {
        // initialParams is itself a JSON *string* inside the command body.
        string initialParams = JsonSerializer.Serialize(new
        {
            accountId,
            roomId = 0,
            sessionId,
            clientType,
            data1 = seeds.Data1,
            data2 = seeds.Data2,
            supportCmd = "130000",
            protocolVer = "1.0",
            data3 = seeds.Data3,
        });

        var body = new
        {
            commandDetail = new
            {
                platform = HalyardEndpoints.CurrentGenPlatformTag,
                duid = consoleDuid,
                commandType = HalyardEndpoints.StreamingCommandType,
                parameters = new { initialParams },
                messageDestination = "SQS",
            },
        };

        var response = await SendJsonAsync<HalyardCommandResponse>(
            HttpMethod.Post, HalyardEndpoints.Commands, body, cancellationToken).ConfigureAwait(false)
            ?? throw new HalyardCloudException("Connect command returned no commandId.");
        return response.CommandId;
    }

    /// <summary>Send a signaling OFFER (candidate list) to the console over the session's message channel.</summary>
    public async Task SendOfferAsync(
        string sessionId,
        string accountId,
        string consoleDuid,
        IReadOnlyList<HalyardCandidate> candidates,
        CancellationToken cancellationToken)
    {
        string offerBody = JsonSerializer.Serialize(new
        {
            action = "OFFER",
            reqId = 1,
            error = 0,
            connRequest = new
            {
                sid = 1,
                peerSid = 0,
                skey = Convert.ToBase64String(new byte[16]),
                natType = 2,
                candidate = candidates.Select(c => new
                {
                    type = c.Type,
                    addr = c.Address,
                    mappedAddr = "0.0.0.0",
                    port = c.Port,
                    mappedPort = 0,
                }).ToArray(),
                localPeerAddr = new { accountId, platform = "REMOTE_PLAY" },
            },
        });

        var body = new
        {
            channel = HalyardEndpoints.SignalingChannel,
            payload = $"ver=1.0, type=text, body={offerBody}",
            to = new[] { new { accountId, deviceUniqueId = consoleDuid, platform = HalyardEndpoints.CurrentGenPlatformTag } },
        };

        string url = $"{HalyardEndpoints.Sessions}/{sessionId}/sessionMessage";
        await SendJsonAsync<object?>(HttpMethod.Post, url, body, cancellationToken, allowEmpty: true).ConfigureAwait(false);
    }

    // ---- transport helpers ----

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body) ?? throw new HalyardCloudException($"Empty/invalid response from {url}.", body);
    }

    private async Task<T?> SendJsonAsync<T>(
        HttpMethod method, string url, object body, CancellationToken cancellationToken, bool allowEmpty = false)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (allowEmpty && string.IsNullOrWhiteSpace(responseBody))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(responseBody);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new HalyardCloudException($"{request.Method} {request.RequestUri} failed ({(int)response.StatusCode}).", body);
        }

        return response;
    }
}
