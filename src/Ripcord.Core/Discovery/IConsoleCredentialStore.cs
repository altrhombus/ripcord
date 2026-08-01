namespace Ripcord.Core.Discovery;

/// <summary>
/// Persists per-console registration key/blob produced by <see cref="IConsolePairingService"/>.
/// Implementations should protect the blob at rest (e.g. DPAPI) since it's the long-lived
/// credential that lets this app act as a registered "controller" for the console.
/// </summary>
public interface IConsoleCredentialStore
{
    Task SaveAsync(string consoleId, byte[] registrationKey, CancellationToken cancellationToken);

    Task<byte[]?> LoadAsync(string consoleId, CancellationToken cancellationToken);

    Task RemoveAsync(string consoleId, CancellationToken cancellationToken);
}
