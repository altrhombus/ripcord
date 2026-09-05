using System.Globalization;
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

    /// <summary>Look up the push front-end host and its keepalive timing (the serveraddr call).</summary>
    public async Task<HalyardPushServerInfo> GetPushServerAsync(CancellationToken cancellationToken)
        => await GetJsonAsync<HalyardPushServerInfo>(HalyardEndpoints.PushServerAddress, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Read back one specific session by id. Carries the <c>X-PSN-SESSION-MANAGER-SESSION-IDS</c> header the
    /// service requires on a session read; a bare <c>GET remotePlaySessions</c> without it is a 400.
    /// </summary>
    public async Task<IReadOnlyList<HalyardCloudSession>> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, HalyardEndpoints.Sessions);
        request.Headers.TryAddWithoutValidation(HalyardEndpoints.SessionIdsHeader, sessionId);

        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<HalyardSessionsResponse>(body);
        return parsed?.RemotePlaySessions ?? [];
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
        // initialParams is itself a JSON *string* inside the command body. Fields and order match a captured
        // vendor command exactly (cap64, re-confirmed against cap96/cap97/cap107): accountId, roomId,
        // sessionId, clientType, data1, data2 — and nothing else. An earlier version also sent
        // supportCmd/protocolVer/data3, which no capture shows here; those belong to the direct handshake, and
        // the three later captures settle it. seeds.Data3 is unused (the command carries only two).
        //
        // Composed by hand rather than serialized from an anonymous type, for one reason that is wire-visible:
        // **accountId is a JSON number, not a string.** Every capture shows a bare 19-digit integer, and a C#
        // `string accountId` serializes as a quoted one — a console that numerically parses this field sees a
        // type it did not expect, which is a candidate for the console accepting the command's wake and then
        // not joining the session. The comma spacing matches the captures too; that part is cosmetic, but it
        // costs nothing to be byte-faithful where we can be.
        //
        // Each string is escaped through the serializer rather than interpolated raw, so a value containing a
        // quote cannot break the document. accountId falls back to a quoted form if it is somehow not numeric,
        // because a malformed number would be worse than the string we sent before.
        string accountIdJson = ulong.TryParse(accountId, out ulong numericAccountId)
            ? numericAccountId.ToString(CultureInfo.InvariantCulture)
            : JsonSerializer.Serialize(accountId);

        string initialParams =
            $"{{\"accountId\":{accountIdJson}, "
            + "\"roomId\":0, "
            + $"\"sessionId\":{JsonSerializer.Serialize(sessionId)}, "
            + $"\"clientType\":{JsonSerializer.Serialize(clientType)}, "
            + $"\"data1\":{JsonSerializer.Serialize(seeds.Data1)}, "
            + $"\"data2\":{JsonSerializer.Serialize(seeds.Data2)}}}";

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

    /// <summary>
    /// Send a signaling OFFER (candidate list) to the console over the session's message channel.
    ///
    /// <para>
    /// <paramref name="sid"/> and <paramref name="reqId"/> are the caller's because a session offers more than
    /// one connection and each needs its own identity. They were fixed at 1 while only the control connection
    /// existed, which was invisible until the A/V leg was added: both offers then claimed stream 1, the console
    /// took our id for that leg from this message, and the second connection collided with the first. It
    /// preludes fine and is never served. The captured client numbers them 1 and 2, with reqIds 1 and 3.
    /// </para>
    /// </summary>
    public async Task SendOfferAsync(
        string sessionId,
        string accountId,
        string consoleDuid,
        IReadOnlyList<HalyardCandidate> candidates,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte> localHashedId = default,
        int reqId = 1,
        int sid = 1)
    {
        // Field-for-field against our own capture of one OFFER, with two deliberate differences noted below.
        // `skey` really is 16 zero bytes at this stage and `mappedAddr` really is the literal "0.0.0.0" — both
        // were verified rather than assumed, because both look like placeholders a reimplementation would be
        // tempted to "fix".
        string offerBody = JsonSerializer.Serialize(new
        {
            action = "OFFER",
            reqId,
            error = 0,
            connRequest = new
            {
                sid,
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

                // Empty in the capture too — the vendor sends the key with no value rather than omitting it.
                defaultRouteMacAddr = string.Empty,
                localPeerAddr = new { accountId, platform = "REMOTE_PLAY" },

                // Sent when the caller supplies one, empty otherwise — which is how this shipped while the
                // field had no job. It has one now: the 9303 control prelude names both peers by the ids they
                // published here, so a client that omits it announces a transport peer the console never heard
                // of. The vendor's own derivation is still [X] (see HalyardLocalHashedId); what matters on the
                // wire is that this value and the prelude's agree.
                localHashedId = localHashedId.IsEmpty
                    ? string.Empty
                    : Convert.ToBase64String(localHashedId.Span),
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


    /// <summary>
    /// Acknowledge a message the peer sent. Every signaling message is answered by a <c>RESULT</c> carrying the
    /// <em>sender's</em> <c>reqId</c> — the capture shows the console acking each of ours and vice versa, and a
    /// negotiation that skips them stalls.
    /// </summary>
    public Task SendResultAsync(
        string sessionId, string accountId, string consoleDuid, int reqId, CancellationToken cancellationToken)
        => SendSignalingAsync(
            sessionId, accountId, consoleDuid,
            "{\"action\":\"RESULT\",\"reqId\":" + reqId + ",\"error\":0,\"connRequest\":{}}",
            cancellationToken);

    /// <summary>
    /// Answer the console's OFFER: we accept the path, naming its stream id as our <c>peerSid</c>.
    ///
    /// <para>
    /// The candidate in an ACCEPT is not another advertisement — it describes the <b>selected pair</b>. The
    /// captured client puts the <em>console's</em> address and port in <c>addr</c>/<c>port</c> and its own in
    /// <c>mappedAddr</c>/<c>mappedPort</c>, which is the opposite of an OFFER's candidate and easy to get
    /// backwards.
    /// </para>
    ///
    /// <para>
    /// <b>The body is hand-built, and it is deliberately malformed.</b> The vendor's own client emits
    /// <c>"localPeerAddr":,</c> — a key with no value, which is not valid JSON — and the console accepts it.
    /// PSN never parses this: the whole <c>body=</c> is an opaque string inside the payload it relays. So this
    /// reproduces the bytes rather than correcting them, because "correcting" it would be an untested deviation
    /// from the only version known to work. Do not tidy this.
    /// </para>
    /// </summary>
    public Task SendAcceptAsync(
        string sessionId,
        string accountId,
        string consoleDuid,
        int reqId,
        int sid,
        int peerSid,
        HalyardCandidate consoleCandidate,
        string localAddress,
        int localPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consoleCandidate);

        string candidate =
            "{\"type\":\"" + consoleCandidate.Type + "\",\"addr\":\"" + consoleCandidate.Address
            + "\",\"mappedAddr\":\"" + localAddress + "\",\"port\":" + consoleCandidate.Port
            + ",\"mappedPort\":" + localPort + "}";

        string body =
            "{\"action\":\"ACCEPT\",\"reqId\":" + reqId + ",\"error\":0,\"connRequest\":{\"sid\":" + sid
            + ",\"peerSid\":" + peerSid + ",\"skey\":\"" + Convert.ToBase64String(new byte[16])
            + "\",\"natType\":0,\"candidate\":[" + candidate
            + "],\"defaultRouteMacAddr\":\"\",\"localPeerAddr\":,\"localHashedId\":\"\"}}";

        return SendSignalingAsync(sessionId, accountId, consoleDuid, body, cancellationToken);
    }

    /// <summary>Post one signaling body on the session's message channel.</summary>
    private Task SendSignalingAsync(
        string sessionId, string accountId, string consoleDuid, string body, CancellationToken cancellationToken)
    {
        var envelope = new
        {
            channel = HalyardEndpoints.SignalingChannel,
            payload = $"ver=1.0, type=text, body={body}",
            to = new[] { new { accountId, deviceUniqueId = consoleDuid, platform = HalyardEndpoints.CurrentGenPlatformTag } },
        };

        string url = $"{HalyardEndpoints.Sessions}/{sessionId}/sessionMessage";
        return SendJsonAsync<object?>(HttpMethod.Post, url, envelope, cancellationToken, allowEmpty: true);
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

        // Sent on every captured vendor cloud call. A required on-wire value rather than a naming choice, and
        // set per-request rather than on the HttpClient because this type does not own the client it is given.
        request.Headers.TryAddWithoutValidation("User-Agent", HalyardEndpoints.CloudUserAgent);

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
