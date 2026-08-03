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

    /// <summary>Add or replace a console by host and persist.</summary>
    public List<PairedConsole> Upsert(PairedConsole console)
    {
        var list = Load();
        list.RemoveAll(c => string.Equals(c.Host, console.Host, StringComparison.OrdinalIgnoreCase));
        list.Add(console);
        Save(list);
        return list;
    }

    public List<PairedConsole> Remove(string host)
    {
        var list = Load();
        list.RemoveAll(c => string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase));
        Save(list);
        return list;
    }
}
