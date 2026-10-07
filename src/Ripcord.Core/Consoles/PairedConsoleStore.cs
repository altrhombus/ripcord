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

    // One read-modify-write at a time. The reachability monitor upserts several consoles at once
    // (Task.WhenAll), account repair and pairing write too, and unserialised they lost each other's changes and
    // shared one temp file (review, 2026-10-05). Static, because the file is one per process whatever the
    // number of store instances.
    private static readonly Lock WriteGate = new();

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

    public bool CanSaveCredentials => _protector is not UnavailableCredentialProtector;

    /// <summary>Encrypt a pairing record for storage.</summary>
    public string EncodeBlob(byte[] pairingRecord) => PairedConsoleBlob.Encode(pairingRecord, _protector);

    /// <summary>Decrypt a stored blob using this store's protector. Null means undecryptable.</summary>
    public byte[]? DecodeBlob(string stored) => PairedConsoleBlob.Decode(stored, _protector);

    public List<PairedConsole> Load()
    {
        try
        {
            return LoadForUpdate();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable store must not brick the app. Only for reading: an update reads with
            // LoadForUpdate, so a passing failure can't turn into a save of an empty list.
            return [];
        }
    }

    public void Save(List<PairedConsole> consoles)
    {
        lock (WriteGate)
        {
            SaveUnlocked(consoles);
        }
    }

    /// <summary>Add or replace a console and persist.</summary>
    public List<PairedConsole> Upsert(PairedConsole console)
    {
        lock (WriteGate)
        {
            List<PairedConsole> list = LoadForUpdate();
            list.RemoveAll(c => IsSameConsole(c, console));
            list.Add(console);
            SaveUnlocked(list);
            return list;
        }
    }

    /// <summary>Forget a console by its <see cref="PairedConsole.Id"/>.</summary>
    public List<PairedConsole> Remove(string id)
    {
        lock (WriteGate)
        {
            List<PairedConsole> list = LoadForUpdate();
            list.RemoveAll(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            SaveUnlocked(list);
            return list;
        }
    }

    /// <summary>
    /// The stored list for a read-modify-write. A file that can't be read right now (another process holding it,
    /// say) throws rather than reading as empty: that read as "no consoles", and the write after it saved a
    /// one-console list over every pairing (review, 2026-10-05). A corrupt file still reads as empty, as Load's
    /// does, since there is nothing in it to keep.
    /// </summary>
    private List<PairedConsole> LoadForUpdate()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        string json = ReadWithRetry(_path);
        try
        {
            return JsonSerializer.Deserialize(json, PairedConsoleContext.Default.ListPairedConsole) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // A sharing violation is usually a moment long (an antivirus scan, a backup), so it is tried a few times.
    private static string ReadWithRetry(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private void SaveUnlocked(List<PairedConsole> consoles)
    {
        // Temp file + move, so an interrupted write cannot destroy the existing pairings. A temp name of its
        // own per write, so two writers can never interleave in one file.
        string tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(consoles, PairedConsoleContext.Default.ListPairedConsole));
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Whether two records are the same physical console: the same <see cref="PairedConsole.Id"/>, which is the
    /// console's own host-id once discovery has supplied one, so it follows the console to a new DHCP lease.
    ///
    /// <para>
    /// The address counts only for a record written before host-ids were stored, whose id IS its address. It
    /// used to count for every record, so with two consoles at home, moving one onto an address the other had
    /// held deleted the other's record and its credential (review, 2026-10-05). Matching on host alone, before
    /// that, duplicated a console every time its address moved.
    /// </para>
    /// </summary>
    internal static bool IsSameConsole(PairedConsole a, PairedConsole b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
        || ((IsAddressKeyed(a) || IsAddressKeyed(b))
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase));

    // A record from before host-ids were stored: its id is its address.
    private static bool IsAddressKeyed(PairedConsole c) => string.Equals(c.Id, c.Host, StringComparison.OrdinalIgnoreCase);
}
