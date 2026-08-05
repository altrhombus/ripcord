using Ripcord.Core;
using Ripcord.Core.Discovery;
using Ripcord.Core.Reactive;
using Ripcord.Protocol.Halyard.Common.Discovery;

namespace Ripcord.Protocol.Halyard.Discovery;

/// <summary>
/// LAN discovery via the SRCH broadcast probe: finds directly-reachable consoles and their LAN
/// address, firmware version, and power state. Complements the cloud console list (which has wake
/// capability but no address); the app merges the two by display name.
///
/// <para>
/// The <paramref name="profile"/> decides which family is searched for. It defaults to PS5, which is what
/// this did unconditionally before the parameter existed — and that was a real bug rather than a limitation:
/// the pairing UI offered PS4 as a choice while the scan behind it could only ever have found a PS5, because
/// the two families answer SRCH on different ports with different version tokens (9302/00030010 vs
/// 987/00020020). To find both, run one service per <see cref="HalyardDiscoveryProfile.All"/> entry.
/// </para>
/// </summary>
public sealed class HalyardLanDiscoveryService(
    TimeSpan? searchWindow = null, HalyardDiscoveryProfile? profile = null) : IConsoleDiscoveryService
{
    private readonly HalyardDiscoveryProfile _profile = profile ?? HalyardDiscoveryProfile.Ps5;
    private readonly HalyardSearchClient _search = new(profile);
    private readonly TimeSpan _window = searchWindow ?? TimeSpan.FromSeconds(2);

    /// <summary>The family this instance searches for.</summary>
    public HalyardDiscoveryProfile Profile => _profile;

    /// <summary>
    /// Consoles are reported as they answer, not in one batch when the search window closes. The window still
    /// runs its full length — a resting console can be slow, and shortening the wait is how consoles get
    /// missed — but a caller showing a list has no reason to sit blank while consoles that already replied
    /// wait for the stragglers.
    /// </summary>
    public IObservable<DiscoveredConsole> Discover(CancellationToken cancellationToken)
        => AsyncObservable.Create<DiscoveredConsole>(async (observer, token) =>
        {
            var relay = new SynchronousProgress<HalyardSearchResult>(result => observer.OnNext(Map(result)));
            await _search.SearchAsync(_window, token, relay).ConfigureAwait(false);
        });

    private static DiscoveredConsole Map(HalyardSearchResult result) => new(
        Id: result.HostId,
        DisplayName: result.HostName,
        // The console's own host-type decides this, not the profile we happened to probe with: a PS4 that
        // answers a PS5-profile search should still be reported as a PS4.
        Platform: PlatformFor(result.HostType),
        IpAddress: result.Address,
        IsAwake: result.IsAwake,
        DiscoveryTransport: DiscoveryTransport.LanBroadcast,
        SystemVersion: result.SystemVersion);

    private static ConsolePlatform PlatformFor(string? hostType) =>
        string.Equals(hostType, HalyardDiscoveryProfile.Ps4.HostType, StringComparison.OrdinalIgnoreCase)
            ? ConsolePlatform.HalyardLegacy
            : ConsolePlatform.Halyard;

    /// <summary>
    /// Invokes its callback on the reporting thread. <see cref="Progress{T}"/> would post to whichever
    /// synchronization context happened to be current when it was constructed, which for an observer that is
    /// meant to fire as replies arrive means arbitrary reordering — and, in a host with no context at all,
    /// delivery after the search has already returned.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
