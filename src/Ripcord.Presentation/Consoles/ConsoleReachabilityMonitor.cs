namespace Ripcord.Presentation.Consoles;

/// <summary>
/// Fills in the live reachability of a set of console cards, and watches a console that was just asked to rest
/// until it settles.
///
/// <para>
/// A snapshot, not a poll: this runs when the list is (re)built and nowhere else. A paired-console list does not
/// change state second to second, and continuous polling would be a poor trade on a battery-powered handheld.
/// The one exception is the rest watch below, which is a bounded one-shot transition watch rather than a poll —
/// it gives up after its budget so it can never become a background loop.
/// </para>
/// </summary>
public sealed class ConsoleReachabilityMonitor
{
    /// <summary>
    /// How long, and how often, to watch a rest-requested console settle. Rest is a very visible physical
    /// action, so a handful of checks over a minute is worth it; more than that and it is a poll.
    /// </summary>
    public static readonly TimeSpan DefaultRestSettleInterval = TimeSpan.FromSeconds(10);

    public const int DefaultRestSettleMaxChecks = 6; // 6 × 10s = 60s

    private readonly IConsoleReachabilityProbe _probe;
    private readonly Func<CancellationToken, Task<IReadOnlyCollection<string>>>? _remotelyAvailable;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _restSettleInterval;
    private readonly int _restSettleMaxChecks;

    /// <param name="delay">
    /// Injected for the same reason <c>SessionController</c> injects one: the rest watch's whole contract is
    /// "six checks, ten seconds apart, then give up", and a test of that must not take a real minute.
    /// </param>
    /// <param name="remotelyAvailable">
    /// The cloud ids of consoles the account service says are available for remote play, asked at most once
    /// per refresh and only when some console failed to answer locally. Null when this build has no account
    /// tier, in which case a console that does not answer is simply offline, exactly as before.
    ///
    /// <para>
    /// A function rather than the account seam itself, because what this needs is one set of ids: taking the
    /// seam would let the monitor make one REST call per card, and the whole point is that it is one call for
    /// the list.
    /// </para>
    /// </param>
    public ConsoleReachabilityMonitor(
        IConsoleReachabilityProbe probe,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? restSettleInterval = null,
        int? restSettleMaxChecks = null,
        Func<CancellationToken, Task<IReadOnlyCollection<string>>>? remotelyAvailable = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _remotelyAvailable = remotelyAvailable;
        _delay = delay ?? Task.Delay;
        _restSettleInterval = restSettleInterval ?? DefaultRestSettleInterval;
        _restSettleMaxChecks = restSettleMaxChecks ?? DefaultRestSettleMaxChecks;
    }

    /// <summary>
    /// Probe every card once and set its reachability. Probes run concurrently so one offline console's timeout
    /// does not delay the others, and each card stays <see cref="ConsoleReachability.Checking"/> until its own
    /// probe resolves.
    /// </summary>
    /// <param name="restRequestedHost">
    /// The console (by host) we asked to rest as the last session ended. That one gets the transition watch
    /// instead of a single probe, because a console that has just been told to rest is still awake for a few
    /// seconds afterwards and a single probe would report it as simply Online.
    /// </param>
    public Task RefreshAsync(
        IReadOnlyList<ConsoleCardViewModel> cards,
        string? restRequestedHost,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cards);

        // Fetched at most once, and only if some console fails to answer locally -- which on the user's own
        // network is never, so the common case costs nothing.
        var remote = new Lazy<Task<IReadOnlyCollection<string>>>(
            () => _remotelyAvailable is null
                ? Task.FromResult<IReadOnlyCollection<string>>([])
                : _remotelyAvailable(cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);

        return Task.WhenAll(cards.Select(card =>
            restRequestedHost is not null
            && string.Equals(card.Console.Host, restRequestedHost, StringComparison.OrdinalIgnoreCase)
                ? WatchRestSettleAsync(card, cancellationToken)
                : ProbeOnceAsync(card, remote, cancellationToken)));
    }

    private async Task ProbeOnceAsync(
        ConsoleCardViewModel card,
        Lazy<Task<IReadOnlyCollection<string>>> remote,
        CancellationToken cancellationToken)
    {
        try
        {
            bool? awake = await _probe.ProbeAsync(card.Console, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            card.Reachability = awake is null
                ? await ClassifySilenceAsync(card, remote, cancellationToken).ConfigureAwait(false)
                : Classify(awake);
        }
        catch (OperationCanceledException)
        {
            // The page was left or the list rebuilt: the card is gone and there is nothing to update.
        }
    }

    /// <summary>
    /// Watch a console we asked to rest until it settles. Ends early the moment it answers from standby. If the
    /// budget runs out — it never rested, or powered off entirely and stopped answering — the card shows whatever
    /// the last probe actually saw rather than being left stuck on the transition. Any probe error or
    /// cancellation just stops the watch; a transition indicator is not worth surfacing failures over.
    /// </summary>
    private async Task WatchRestSettleAsync(ConsoleCardViewModel card, CancellationToken cancellationToken)
    {
        card.Reachability = ConsoleReachability.PreparingForRest;

        try
        {
            for (int check = 0; check < _restSettleMaxChecks; check++)
            {
                await _delay(_restSettleInterval, cancellationToken).ConfigureAwait(false);

                bool? awake = await _probe.ProbeAsync(card.Console, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ConsoleReachability reach = Classify(awake);

                // Standby is the clean end state of a rest transition — settle and stop early. Online means it
                // has not gone down yet, and a bare no-reply can be a momentary gap mid-transition, so keep
                // showing "Going to sleep…" for both and let the budget decide.
                if (reach == ConsoleReachability.Resting)
                {
                    card.Reachability = ConsoleReachability.Resting;
                    return;
                }

                if (check == _restSettleMaxChecks - 1)
                {
                    // Gave up: report the ground truth we last saw.
                    card.Reachability = reach;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // As above — the watch is best-effort.
        }
    }

    /// <summary>
    /// What a console's silence means. On this network it means offline; anywhere else it means nothing at all,
    /// because a discovery probe cannot leave the subnet — so before calling a console unreachable, ask the one
    /// party that can see it from here.
    ///
    /// <para>
    /// A console with no cloud id was paired by code and the account service has no name for it, so there is
    /// nothing to ask and silence really is offline. Any failure asking is treated the same way: a status dot
    /// is not worth surfacing an error over, and offline is what the user would have seen anyway.
    /// </para>
    /// </summary>
    private static async Task<ConsoleReachability> ClassifySilenceAsync(
        ConsoleCardViewModel card,
        Lazy<Task<IReadOnlyCollection<string>>> remote,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(card.Console.CloudDeviceId))
        {
            return ConsoleReachability.Offline;
        }

        try
        {
            IReadOnlyCollection<string> available = await remote.Value.ConfigureAwait(false);
            return !cancellationToken.IsCancellationRequested
                   && available.Contains(card.Console.CloudDeviceId, StringComparer.OrdinalIgnoreCase)
                ? ConsoleReachability.Away
                : ConsoleReachability.Offline;
        }
        catch (Exception)
        {
            return ConsoleReachability.Offline;
        }
    }

    private static ConsoleReachability Classify(bool? awake) => awake switch
    {
        true => ConsoleReachability.Online,
        false => ConsoleReachability.Resting,
        null => ConsoleReachability.Offline,
    };
}
