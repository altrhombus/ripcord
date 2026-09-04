using System.Text.Json;

namespace Ripcord.Cloud.Halyard;

/// <summary>
/// Parses the console's <c>customData1</c> push notification — the frame that carries the account
/// ("web"/no-PIN) registration seed.
///
/// <para>
/// After the client sends the connect command (with its ephemeral <c>data1</c>/<c>data2</c>), the console
/// field-encrypts the registration seed with them and publishes the ciphertext as <c>customData1</c> — a
/// <b>double-base64</b> value — on the account's push channel, in a
/// <c>psn:sessionManager:sys:rps:customData1:updated</c> notification. This pulls that value out; the
/// protocol layer (its <c>HalyardAccountSeedDelivery</c>) decodes and decrypts it to the seed, because this
/// REST/signaling layer holds no crypto. Every other push frame is not one of these and parses to null.
/// </para>
/// </summary>
public static class HalyardCustomDataNotification
{
    /// <summary>The dataType suffix marking a push notification as a customData1 update.</summary>
    private const string CustomData1DataType = "rps:customData1:updated";

    /// <summary>The dataType suffix marking a notification that somebody joined the session.</summary>
    private const string MemberJoinedDataType = "rps:members:created";

    /// <summary>The platform tag a PS5 console joins under. A required on-wire value.</summary>
    private const string ConsolePlatform = "PROSPERO";

    /// <summary>
    /// Return the raw (double-base64) <c>customData1</c> value from a push frame, or null when the frame is
    /// not a customData1 update (the common case) or is malformed. Null is not an error.
    /// </summary>
    public static string? TryParseCustomData1(string pushFrame)
    {
        if (string.IsNullOrWhiteSpace(pushFrame))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(pushFrame);
            JsonElement root = doc.RootElement;

            if (!EndsWith(root, "dataType", CustomData1DataType)
                || !root.TryGetProperty("body", out JsonElement body)
                || !body.TryGetProperty("data", out JsonElement data)
                || !data.TryGetProperty("customData1", out JsonElement value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? raw = value.GetString();
            return string.IsNullOrEmpty(raw) ? null : raw;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this frame says the <b>console</b> has joined the session.
    ///
    /// <para>
    /// Worth surfacing on its own because it is the earliest moment a client can usefully act. A
    /// <c>sessionMessage</c> may only be sent to a member, so an OFFER before this 404s; and the console begins
    /// opening its own control association shortly afterwards, so a client that waits for the console's OFFER
    /// before sending its own has already given up the initiative. The frame names the joining platform —
    /// our own join arrives here too, tagged <c>REMOTE_PLAY</c>, and must not be mistaken for the console's.
    /// </para>
    /// </summary>
    public static bool IsConsoleJoined(string pushFrame)
    {
        if (string.IsNullOrWhiteSpace(pushFrame))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(pushFrame);
            JsonElement root = doc.RootElement;

            if (!EndsWith(root, "dataType", MemberJoinedDataType)
                || !root.TryGetProperty("body", out JsonElement body)
                || !body.TryGetProperty("data", out JsonElement data)
                || !data.TryGetProperty("members", out JsonElement members)
                || members.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement member in members.EnumerateArray())
            {
                if (member.TryGetProperty("platform", out JsonElement platform)
                    && platform.ValueKind == JsonValueKind.String
                    && string.Equals(platform.GetString(), ConsolePlatform, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool EndsWith(JsonElement parent, string name, string suffix)
        => parent.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && element.GetString() is { } value
            && value.EndsWith(suffix, StringComparison.Ordinal);
}
