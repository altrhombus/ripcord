namespace Ripcord.Core.Discovery;

/// <summary>
/// Platform-specific discovery backend (LAN broadcast/cloud relay, local discovery, etc).
/// Registered per <see cref="ConsolePlatform"/> so the app shell never branches on platform directly.
/// </summary>
public interface IConsoleDiscoveryService
{
    IObservable<DiscoveredConsole> Discover(CancellationToken cancellationToken);
}
