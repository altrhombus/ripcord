using Ripcord.Core;
using Ripcord.Core.Discovery;
using Ripcord.Core.Reactive;
using Ripcord.Protocol.Halyard.Common.Discovery;

namespace Ripcord.Protocol.Halyard.Discovery;

/// <summary>
/// LAN discovery via the SRCH broadcast probe: finds directly-reachable consoles and their LAN
/// address, firmware version, and power state. Complements the cloud console list (which has wake
/// capability but no address); the app merges the two by display name.
/// </summary>
public sealed class HalyardLanDiscoveryService(TimeSpan? searchWindow = null) : IConsoleDiscoveryService
{
    private readonly HalyardSearchClient _search = new();
    private readonly TimeSpan _window = searchWindow ?? TimeSpan.FromSeconds(2);

    public IObservable<DiscoveredConsole> Discover(CancellationToken cancellationToken)
        => AsyncObservable.Create<DiscoveredConsole>(async (observer, token) =>
        {
            IReadOnlyList<HalyardSearchResult> results = await _search.SearchAsync(_window, token).ConfigureAwait(false);
            foreach (HalyardSearchResult result in results)
            {
                observer.OnNext(new DiscoveredConsole(
                    Id: result.HostId,
                    DisplayName: result.HostName,
                    Platform: ConsolePlatform.Halyard,
                    IpAddress: result.Address,
                    IsAwake: result.IsAwake,
                    DiscoveryTransport: DiscoveryTransport.LanBroadcast));
            }
        });
}
