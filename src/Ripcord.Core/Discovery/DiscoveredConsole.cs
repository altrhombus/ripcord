using System.Net;

namespace Ripcord.Core.Discovery;

public enum DiscoveryTransport
{
    LanBroadcast,
    Mdns,
    WanRelay,
}

/// <param name="Id">The console's own stable identifier, where the transport supplies one.</param>
/// <param name="SystemVersion">
/// The firmware version the console reported, when it reported one. Optional and trailing so adding it did
/// not disturb existing callers; null for any transport that does not carry it (the cloud console list, for
/// one). Informational only — nothing branches on it.
/// </param>
public sealed record DiscoveredConsole(
    string Id,
    string DisplayName,
    ConsolePlatform Platform,
    IPAddress IpAddress,
    bool IsAwake,
    DiscoveryTransport DiscoveryTransport,
    string? SystemVersion = null);
