using Ripcord.Core.Security;

namespace Ripcord.Core.Consoles;

/// <summary>
/// In-memory paired-console store for tests and for hosts that do not persist. The counterpart to
/// <see cref="Settings.InMemorySettingsStore"/>.
///
/// <para>
/// Uses a real <see cref="ICredentialProtector"/> — <see cref="PlaintextCredentialProtector"/> by default — so
/// the encode/decode round trip still runs. That matters: a fake that skipped wrapping entirely would let a test
/// pass while the production path stored something the production path could not read back.
/// </para>
/// </summary>
public sealed class InMemoryPairedConsoleStore : IPairedConsoleStore
{
    private readonly ICredentialProtector _protector;
    private List<PairedConsole> _consoles;

    public InMemoryPairedConsoleStore(
        IEnumerable<PairedConsole>? initial = null,
        ICredentialProtector? protector = null)
    {
        _consoles = initial?.ToList() ?? [];
        _protector = protector ?? new PlaintextCredentialProtector();
    }

    /// <summary>How many times <see cref="Save"/> has been called, so a test can assert a write happened.</summary>
    public int SaveCount { get; private set; }

    public string ProtectionDescription => _protector.Description;

    public bool CredentialsEncrypted => _protector.IsRealProtection;

    // A copy, not the backing list: callers mutate what Load() returns (Upsert and Remove both do), and handing
    // out the live list would let a caller edit stored state without a Save.
    public List<PairedConsole> Load() => [.. _consoles];

    public void Save(List<PairedConsole> consoles)
    {
        ArgumentNullException.ThrowIfNull(consoles);
        _consoles = [.. consoles];
        SaveCount++;
    }

    public List<PairedConsole> Upsert(PairedConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var list = Load();
        list.RemoveAll(c => PairedConsoleStore.IsSameConsole(c, console));
        list.Add(console);
        Save(list);
        return list;
    }

    public List<PairedConsole> Remove(string id)
    {
        var list = Load();
        list.RemoveAll(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        Save(list);
        return list;
    }

    public string EncodeBlob(byte[] pairingRecord) => PairedConsoleBlob.Encode(pairingRecord, _protector);

    public byte[]? DecodeBlob(string stored) => PairedConsoleBlob.Decode(stored, _protector);
}
