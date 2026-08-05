using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Core.Platform;
using Ripcord.Core.Security;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord_App.Services;

/// <summary>
/// A console this client has paired with, plus its pairing-record credential.
/// </summary>
/// <param name="CredentialBlob">
/// The serialized pairing record as stored on disk. Encrypted at rest (see <see cref="PairedConsoleStore"/>),
/// so this is ciphertext rather than the bare hex it used to be — never log or display it.
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

    /// <summary>Rehydrate the pairing record (registkey + companion + keytype) from the stored blob.</summary>
    public HalyardPairingRecord? ToRecord(ICredentialProtector protector)
    {
        byte[]? plain = PairedConsoleStore.DecodeBlob(StoredBlob, protector);
        return plain is not null && HalyardPairingRecord.TryDeserialize(plain, out var r) ? r : null;
    }
}

/// <summary>
/// Local persistence for paired consoles: a JSON file in the platform config directory, with the credential
/// blob encrypted at rest via <see cref="ICredentialProtector"/> (DPAPI on Windows).
///
/// <para>
/// Two things changed here beyond encryption. Writes are atomic (temp file + move) so a crash or a full disk
/// cannot leave a truncated file that loses every pairing; and the location comes from
/// <see cref="IPlatformPaths"/> rather than a hardcoded <c>%LocalAppData%</c>.
/// </para>
/// </summary>
internal sealed partial class PairedConsoleStore
{
    /// <summary>Marks a blob as ciphertext, distinguishing it from a pre-encryption plaintext hex blob.</summary>
    internal const string ProtectedPrefix = "dpapi:";

    // Source-generated: the list of paired consoles. Unreadable under trimming, the app would present a
    // paired install as having no consoles at all.
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(List<PairedConsole>))]
    private partial class PairedConsoleContext : JsonSerializerContext;

    private readonly string _path;
    private readonly ICredentialProtector _protector;

    public PairedConsoleStore(IPlatformPaths? paths = null, ICredentialProtector? protector = null)
    {
        IPlatformPaths resolved = paths ?? new DefaultPlatformPaths();
        _path = Path.Combine(resolved.ConfigDirectory, "consoles.json");
        _protector = protector ?? CredentialProtection.ForCurrentPlatform();
    }

    /// <summary>How credentials are protected at rest, for the settings UI to report honestly.</summary>
    public string ProtectionDescription => _protector.Description;

    /// <summary>True when the stored credentials are actually encrypted on this platform.</summary>
    public bool CredentialsEncrypted => _protector.IsRealProtection;

    /// <summary>Encrypt a pairing record for storage.</summary>
    public string EncodeBlob(byte[] pairingRecord)
        => ProtectedPrefix + Convert.ToBase64String(_protector.Protect(pairingRecord));

    /// <summary>
    /// Decrypt a stored blob, tolerating a legacy plaintext-hex value so upgrading does not silently drop
    /// existing pairings. Null means undecryptable — treat as not paired.
    /// </summary>
    internal static byte[]? DecodeBlob(string stored, ICredentialProtector protector)
    {
        try
        {
            return stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal)
                ? protector.Unprotect(Convert.FromBase64String(stored[ProtectedPrefix.Length..]))
                : Convert.FromHexString(stored);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Rehydrate a console's pairing record using this store's protector.</summary>
    public HalyardPairingRecord? ToRecord(PairedConsole console) => console.ToRecord(_protector);

    /// <summary>Decrypt a stored blob using this store's protector. Null means undecryptable.</summary>
    public byte[]? DecodeBlob(string stored) => DecodeBlob(stored, _protector);

    public List<PairedConsole> Load()
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(_path), PairedConsoleContext.Default.ListPairedConsole) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable store must not brick the app; the next save rewrites it.
            return [];
        }
    }

    public void Save(List<PairedConsole> consoles)
    {
        // Temp file + move, so an interrupted write cannot destroy the existing pairings.
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(consoles, PairedConsoleContext.Default.ListPairedConsole));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Add or replace a console and persist.</summary>
    public List<PairedConsole> Upsert(PairedConsole console)
    {
        var list = Load();
        list.RemoveAll(c => IsSameConsole(c, console));
        list.Add(console);
        Save(list);
        return list;
    }

    /// <summary>Forget a console by its <see cref="PairedConsole.Id"/>.</summary>
    public List<PairedConsole> Remove(string id)
    {
        var list = Load();
        list.RemoveAll(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        Save(list);
        return list;
    }

    /// <summary>
    /// Whether two records are the same physical console. Matching on either identifier is deliberate and
    /// covers the two ways a re-pair arrives: same box at the same address (<see cref="PairedConsole.Host"/>
    /// matches), or same box that has since been handed a different DHCP lease (<see cref="PairedConsole.Id"/>
    /// matches, because it is the console's own host-id once discovery has supplied one).
    ///
    /// <para>
    /// Matching on host alone — which is what this did before ids were stable — silently duplicated a console
    /// every time its address moved. Matching on id alone would have stopped recognising every record written
    /// before host-ids were stored, since for those <c>Id == Host ==</c> the address.
    /// </para>
    /// </summary>
    private static bool IsSameConsole(PairedConsole a, PairedConsole b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
}
