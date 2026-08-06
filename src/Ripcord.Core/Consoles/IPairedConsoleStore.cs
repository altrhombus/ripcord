namespace Ripcord.Core.Consoles;

/// <summary>
/// Local persistence for the consoles this client has paired with.
///
/// <para>
/// A seam rather than a concrete class for the same reason <see cref="Settings.ISettingsStore"/> is one: it lets
/// the console list, the pairing flow and the session layer be exercised without touching the user's real
/// <c>consoles.json</c> or their DPAPI keys. <see cref="InMemoryPairedConsoleStore"/> is the substitute.
/// </para>
/// </summary>
public interface IPairedConsoleStore
{
    /// <summary>How credentials are protected at rest, so the settings UI can report it honestly.</summary>
    string ProtectionDescription { get; }

    /// <summary>True when the stored credentials are actually encrypted on this platform.</summary>
    bool CredentialsEncrypted { get; }

    /// <summary>Every paired console. Empty when nothing has been paired, or when the store is unreadable.</summary>
    List<PairedConsole> Load();

    /// <summary>Replace the stored set wholesale.</summary>
    void Save(List<PairedConsole> consoles);

    /// <summary>Add or replace one console and persist. Returns the resulting set.</summary>
    List<PairedConsole> Upsert(PairedConsole console);

    /// <summary>Forget a console by its <see cref="PairedConsole.Id"/>. Returns the resulting set.</summary>
    List<PairedConsole> Remove(string id);

    /// <summary>Wrap a serialized pairing record for storage.</summary>
    string EncodeBlob(byte[] pairingRecord);

    /// <summary>Unwrap a stored blob. Null means undecryptable — treat as not paired.</summary>
    byte[]? DecodeBlob(string stored);
}
