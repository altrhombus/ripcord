using System;
using System.Threading;
using System.Threading.Tasks;
using Ripcord.Core.Discovery;

namespace Ripcord_App.Services;

/// <summary>
/// Bridges the app's paired-console persistence (<see cref="PairedConsoleStore"/>, written by the pairing UI)
/// to the session layer's <see cref="IConsoleCredentialStore"/> seam. The session only ever <em>loads</em>
/// the credential blob (the serialized pairing record) for the console it is connecting to; pairing itself
/// is what writes it, so this stays read-only for save.
/// </summary>
internal sealed class PairedConsoleCredentialStore : IConsoleCredentialStore
{
    private readonly PairedConsoleStore _store;

    public PairedConsoleCredentialStore(PairedConsoleStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<byte[]?> LoadAsync(string consoleId, CancellationToken cancellationToken)
    {
        // The session keys by ConsoleId (= the console host); paired consoles store both Id and Host as that host.
        PairedConsole? console = _store.Load().Find(c =>
            string.Equals(c.Id, consoleId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Host, consoleId, StringComparison.OrdinalIgnoreCase));

        // Decrypt through the store's protector; null (wrong user/machine, corrupt entry) surfaces as
        // "not paired", which the session reports cleanly instead of throwing mid-connect.
        // StoredBlob, not CredentialBlob: it falls back to the pre-encryption property name so an existing
        // consoles.json keeps working after the upgrade.
        byte[]? blob = string.IsNullOrEmpty(console?.StoredBlob)
            ? null
            : _store.DecodeBlob(console!.StoredBlob);
        return Task.FromResult(blob);
    }

    public Task SaveAsync(string consoleId, byte[] registrationKey, CancellationToken cancellationToken)
        => throw new NotSupportedException("Credentials are persisted by the pairing flow via PairedConsoleStore.");

    public Task RemoveAsync(string consoleId, CancellationToken cancellationToken)
    {
        _store.Remove(consoleId); // PairedConsoleStore keys removal by host
        return Task.CompletedTask;
    }
}
