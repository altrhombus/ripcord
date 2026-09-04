using System.Text.Json;

namespace Ripcord.Cloud.Halyard;

/// <summary>One candidate a peer advertises in a signaling OFFER — how it can be reached.</summary>
/// <param name="Type">
/// <c>LOCAL</c> (LAN address), <c>STATIC</c> (reflexive/relay address), or <c>STUN</c> (a STUN-gathered
/// reflexive candidate). Kept as the wire string rather than an enum: an unknown type must round-trip and be
/// ignored rather than fail the parse, since the console may add candidate kinds we have not seen.
/// </param>
/// <param name="Address">The candidate address.</param>
/// <param name="Port">The candidate port. The console advertises on 9303 (the account-route port).</param>
public sealed record HalyardSignalingCandidate(string Type, string Address, int Port);

/// <summary>
/// A signaling message from the peer, recovered from the push channel — the inbound half of the rendezvous
/// that <see cref="HalyardCloudClient.SendOfferAsync"/> is the outbound half of.
///
/// <para>
/// The console does not answer an OFFER with an ANSWER; the exchange is symmetric — <b>each side sends its own
/// OFFER carrying its own candidates</b>, then acknowledges with RESULT and ACCEPT. So the message we care about
/// most is the console's <c>OFFER</c>: it is where the console's reachable addresses arrive, and until the push
/// channel was decoded it was the one piece of the connect flow we could not read.
/// </para>
/// </summary>
/// <param name="Action">The state-machine step: <c>OFFER</c>, <c>ACCEPT</c>, <c>RESULT</c>.</param>
/// <param name="ReqId">The request sequence number (OFFER/RESULT reqId 1, ACCEPT/RESULT reqId 2).</param>
/// <param name="FromPlatform">
/// Who sent it — <c>PROSPERO</c> (PS5), a PS4 tag, or <c>REMOTE_PLAY</c> (our own message echoed back to us over
/// the same channel). The last is why this field matters: the push channel delivers notifications about the
/// client's own posted messages too, and those must not be mistaken for the console's.
/// </param>
/// <param name="Candidates">The peer's advertised candidates. Empty for RESULT/ACCEPT acknowledgements.</param>
/// <param name="SessionKey">
/// The <c>skey</c> — 16 bytes when the peer has filled it in (the console's OFFER carries a real one; an
/// all-zero <c>skey</c> is the not-yet-populated placeholder our own OFFER sends). Null when absent.
/// </param>
/// <param name="LocalHashedId">The <c>localHashedId</c> — 20 bytes in the console's OFFER. Null when absent.</param>
/// <param name="Sid">
/// The peer's own stream id for this connection. Needed because the negotiation is a real offer/accept: our
/// <c>ACCEPT</c> has to name the console's <c>sid</c> as its <c>peerSid</c>, and without that the console has
/// nothing to match our transport against.
/// </param>
/// <param name="PeerSid">The id the peer believes is ours. Zero in an OFFER, populated in an ACCEPT.</param>
public sealed record HalyardSignalingMessage(
    string Action,
    int ReqId,
    string? FromPlatform,
    IReadOnlyList<HalyardSignalingCandidate> Candidates,
    byte[]? SessionKey,
    byte[]? LocalHashedId,
    int Sid = 0,
    int PeerSid = 0)
{
    /// <summary>The dataType suffix that marks a push notification as carrying a signaling message.</summary>
    private const string SessionMessageDataType = "sessionMessage:created";

    /// <summary>The signaling channel this message must be on to be ours.</summary>
    private const string SignalingChannel = "remote_play:1";

    /// <summary>True when this is the console's OFFER — the message whose candidates are worth acting on.</summary>
    public bool IsConsoleOffer =>
        string.Equals(Action, "OFFER", StringComparison.Ordinal)
        && Candidates.Count > 0
        && !string.Equals(FromPlatform, "REMOTE_PLAY", StringComparison.Ordinal);

    /// <summary>
    /// Parse one push-channel frame into a signaling message, or null when the frame is not one — a
    /// presence/members/customData notification, a message on another channel, or malformed. Null is the
    /// common case (most push frames are not signaling) and is not an error.
    ///
    /// <para>
    /// Uses <see cref="JsonDocument"/> rather than typed deserialization on purpose: the frame nests a JSON
    /// document inside a string field (<c>payload = "ver=1.0, type=text, body={…}"</c>), the shape varies by
    /// action, and unknown fields must be tolerated. A forgiving reader over a semi-structured envelope is the
    /// right tool, and it stays trim-safe without source-gen contexts for a shape this irregular.
    /// </para>
    /// </summary>
    public static HalyardSignalingMessage? TryParse(string pushFrame)
    {
        if (string.IsNullOrWhiteSpace(pushFrame))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(pushFrame);
            JsonElement root = doc.RootElement;

            if (!EndsWith(root, "dataType", SessionMessageDataType)
                || !root.TryGetProperty("body", out JsonElement body)
                || !body.TryGetProperty("data", out JsonElement data)
                || !data.TryGetProperty("sessionMessage", out JsonElement sessionMessage))
            {
                return null;
            }

            if (!StringEquals(sessionMessage, "channel", SignalingChannel)
                || !sessionMessage.TryGetProperty("payload", out JsonElement payloadElement))
            {
                return null;
            }

            string? inner = ExtractBody(payloadElement.GetString());
            if (inner is null)
            {
                return null;
            }

            string? fromPlatform = data.TryGetProperty("customProperties", out JsonElement custom)
                && custom.TryGetProperty("from", out JsonElement from)
                && from.TryGetProperty("platform", out JsonElement platform)
                ? platform.GetString()
                : null;

            return ParseConnRequest(inner, fromPlatform);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Pull the JSON document out of the <c>payload</c> string's <c>body=</c> segment.</summary>
    private static string? ExtractBody(string? payload)
    {
        if (payload is null)
        {
            return null;
        }

        // The payload is "ver=1.0, type=text, body={…}" — everything after the first "body=" is the JSON,
        // which may itself contain "=" so a split on "=" would truncate it.
        int marker = payload.IndexOf("body=", StringComparison.Ordinal);
        return marker < 0 ? null : payload[(marker + "body=".Length)..].TrimStart();
    }

    private static HalyardSignalingMessage? ParseConnRequest(string inner, string? fromPlatform)
    {
        using var doc = JsonDocument.Parse(inner);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("action", out JsonElement actionElement))
        {
            return null;
        }

        string action = actionElement.GetString() ?? string.Empty;
        int reqId = root.TryGetProperty("reqId", out JsonElement r) && r.TryGetInt32(out int rv) ? rv : 0;

        var candidates = new List<HalyardSignalingCandidate>();
        byte[]? skey = null;
        byte[]? hashedId = null;
        int sid = 0;
        int peerSid = 0;

        if (root.TryGetProperty("connRequest", out JsonElement conn))
        {
            skey = DecodeBase64(conn, "skey");
            hashedId = DecodeBase64(conn, "localHashedId");
            sid = conn.TryGetProperty("sid", out JsonElement sidElement) && sidElement.TryGetInt32(out int sv) ? sv : 0;
            peerSid = conn.TryGetProperty("peerSid", out JsonElement peerElement)
                      && peerElement.TryGetInt32(out int pv2) ? pv2 : 0;

            if (conn.TryGetProperty("candidate", out JsonElement candidateArray)
                && candidateArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in candidateArray.EnumerateArray())
                {
                    string? type = c.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
                    string? addr = c.TryGetProperty("addr", out JsonElement a) ? a.GetString() : null;
                    int port = c.TryGetProperty("port", out JsonElement p) && p.TryGetInt32(out int pv) ? pv : 0;

                    if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(addr))
                    {
                        candidates.Add(new HalyardSignalingCandidate(type, addr, port));
                    }
                }
            }
        }

        return new HalyardSignalingMessage(action, reqId, fromPlatform, candidates, skey, hashedId, sid, peerSid);
    }

    private static byte[]? DecodeBase64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) || element.GetString() is not { } value
            || value.Length == 0)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool EndsWith(JsonElement obj, string property, string suffix)
        => obj.TryGetProperty(property, out JsonElement e)
           && e.GetString() is { } s
           && s.EndsWith(suffix, StringComparison.Ordinal);

    private static bool StringEquals(JsonElement obj, string property, string expected)
        => obj.TryGetProperty(property, out JsonElement e)
           && string.Equals(e.GetString(), expected, StringComparison.Ordinal);
}
