using System.Text.Json.Serialization;

namespace Ripcord.Core.Consoles;

/// <summary>
/// A console this client has paired with, plus its pairing-record credential.
/// </summary>
/// <param name="CredentialBlob">
/// The serialized pairing record as stored on disk. Encrypted at rest (see <see cref="PairedConsoleBlob"/>), so
/// this is ciphertext rather than the bare hex it used to be — never log or display it.
/// </param>
public sealed record PairedConsole(string Id, string Name, string Host, string Platform, string CredentialBlob)
{
    /// <summary>
    /// The property this field was called before it held ciphertext. Kept purely so an existing
    /// <c>consoles.json</c> still deserializes: without it, every previously paired console would come back with
    /// a null blob and the user would silently have to re-pair. Never written back out.
    /// </summary>
    [JsonPropertyName("CredentialBlobHex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyCredentialBlobHex { get; init; }

    // ------------------------------------------------------------------------------------------------
    // Everything below was added when the console list learned to show consoles apart from one another.
    //
    // All of it is optional, and added as init properties rather than positional parameters on purpose:
    // the primary constructor keeps its five arguments, so no existing call site changes, and an existing
    // consoles.json deserializes with these simply absent. That is the same back-compat discipline
    // LegacyCredentialBlobHex above demonstrates, and it is what makes the upgrade path structural rather
    // than something anyone has to remember to test. Keep it that way — a required field here means every
    // already-paired console silently disappears from the list on upgrade.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// What the user chose to call this console. Wins over everything, including what the console calls
    /// itself: two consoles that both ship as "PS5-8A2F" are exactly the case this exists for.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Nickname { get; init; }

    /// <summary>The <c>host-name</c> the console broadcast over SRCH when it was paired.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReportedName { get; init; }

    /// <summary>
    /// The console's own <c>host-id</c>. Stable across a DHCP lease change, unlike <see cref="Host"/> — which
    /// is why <see cref="Id"/> is set from this when discovery supplies it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HostId { get; init; }

    /// <summary>The system version reported at pairing time. Shown in the details flyout; never acted on.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemVersion { get; init; }

    /// <summary>When a stream was last started to this console, for the "played 2 hours ago" line.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastConnectedUtc { get; init; }

    /// <summary>
    /// What to show as this console's name. Falls back through nickname → what the console calls itself →
    /// <see cref="Name"/>, which for records written before this existed is the family label ("PlayStation 5").
    /// So an upgraded install looks exactly as it did, and gets better the first time it is re-discovered.
    /// </summary>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Nickname) ? Nickname!
        : !string.IsNullOrWhiteSpace(ReportedName) ? ReportedName!
        : Name;

    /// <summary>
    /// The stored blob, preferring the current property and falling back to the pre-encryption one. Use this
    /// rather than <see cref="CredentialBlob"/> when reading, so upgrades keep working.
    /// </summary>
    [JsonIgnore]
    public string StoredBlob =>
        !string.IsNullOrEmpty(CredentialBlob) ? CredentialBlob : LegacyCredentialBlobHex ?? string.Empty;
}
