namespace Ripcord.Core.Discovery;

/// <summary>
/// Marker interface for the platform-specific pairing payload (e.g. account credential + on-console
/// PIN for one console family; account/pairing code for the other). Each protocol backend defines
/// its own concrete credential type behind this marker.
/// </summary>
public interface IPairingCredentials
{
}

public sealed record PairingResult(bool Succeeded, string? FailureReason, byte[]? RegistrationKey);

/// <summary>
/// One-time registration flow, distinct from the per-launch session connect in
/// <see cref="Sessions.IStreamingSession"/>.
/// </summary>
public interface IConsolePairingService
{
    Task<PairingResult> RegisterAsync(
        DiscoveredConsole console,
        IPairingCredentials credentials,
        CancellationToken cancellationToken);
}
