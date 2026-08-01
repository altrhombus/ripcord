using System.Net;
using Ripcord.Core;
using Ripcord.Core.Discovery;
using Ripcord.Core.Reactive;

namespace Ripcord.Cloud.Halyard;

/// <summary>
/// Cloud-backed discovery: lists the account's consoles via the cloud API. Works off-LAN and reports
/// wake capability, but has no LAN address (that comes from the LAN SRCH/mDNS discovery, merged by
/// display name). Emits one <see cref="DiscoveredConsole"/> per remote-play-enabled console.
/// </summary>
public sealed class HalyardCloudDiscoveryService(HalyardCloudClient cloud) : IConsoleDiscoveryService
{
    private readonly HalyardCloudClient _cloud = cloud;

    public IObservable<DiscoveredConsole> Discover(CancellationToken cancellationToken)
        => AsyncObservable.Create<DiscoveredConsole>(async (observer, ct) =>
        {
            IReadOnlyList<HalyardConsoleClient> consoles = await _cloud.ListConsolesAsync(ct).ConfigureAwait(false);
            foreach (HalyardConsoleClient console in consoles)
            {
                if (!console.RemotePlayEnabled)
                {
                    continue;
                }

                observer.OnNext(new DiscoveredConsole(
                    Id: console.Duid,
                    DisplayName: console.Device.Name,
                    Platform: ConsolePlatform.Halyard,
                    IpAddress: IPAddress.None, // resolved by LAN discovery, merged by name
                    IsAwake: console.CanWake, // wake-capable; actual awake state confirmed on connect
                    DiscoveryTransport: DiscoveryTransport.WanRelay));
            }
        });
}
