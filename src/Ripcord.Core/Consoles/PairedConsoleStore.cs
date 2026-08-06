using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Core.Platform;
using Ripcord.Core.Security;

namespace Ripcord.Core.Consoles;

/// <summary>
/// Local persistence for paired consoles: a JSON file in the platform config directory, with the credential
/// blob encrypted at rest via <see cref="ICredentialProtector"/> (DPAPI on Windows).
///
/// <para>
/// Two things changed here beyond encryption. Writes are atomic (temp file + move) so a crash or a full disk
/// cannot leave a truncated file that loses every pairing; and the location comes from
/// <see cref="IPlatformPaths"/> rather than a hardcoded <c>%LocalAppData%</c>.
/// </para>
///
/// <para>
/// Lives in Ripcord.Core rather than in the app, and is public rather than internal, because three unrelated
/// callers need it: the WinUI app, a future native front end for another OS, and <c>Ripcord.ProtocolLab</c> —
/// which is a plain net10.0 console tool and should not have to reference a presentation assembly just to read
/// the console list. It is the structural twin of <see cref="Settings.SettingsStore"/> next door, down to the
/// temp-file write and the fall-back-rather-than-throw read.
/// </para>
/// </summary>
public sealed partial class PairedConsoleStore : IPairedConsoleStore
{
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
    public string EncodeBlob(byte[] pairingRecord) => PairedConsoleBlob.Encode(pairingRecord, _protector);

    /// <summary>Decrypt a stored blob using this store's protector. Null means undecryptable.</summary>
    public byte[]? DecodeBlob(string stored) => PairedConsoleBlob.Decode(stored, _protector);

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
    internal static bool IsSameConsole(PairedConsole a, PairedConsole b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
}
