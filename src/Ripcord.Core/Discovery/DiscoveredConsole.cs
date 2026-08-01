using System.Net;

namespace Ripcord.Core.Discovery;

public enum DiscoveryTransport
{
    LanBroadcast,
    Mdns,
    WanRelay,
}

public sealed record DiscoveredConsole(
    string Id,
    string DisplayName,
    ConsolePlatform Platform,
    IPAddress IpAddress,
    bool IsAwake,
    DiscoveryTransport DiscoveryTransport);
