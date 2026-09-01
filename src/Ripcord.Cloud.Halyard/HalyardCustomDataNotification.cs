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

    private static bool EndsWith(JsonElement parent, string name, string suffix)
        => parent.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && element.GetString() is { } value
            && value.EndsWith(suffix, StringComparison.Ordinal);
}
