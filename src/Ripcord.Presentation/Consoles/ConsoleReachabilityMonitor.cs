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
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _restSettleInterval;
    private readonly int _restSettleMaxChecks;

    /// <param name="delay">
    /// Injected for the same reason <c>SessionController</c> injects one: the rest watch's whole contract is
    /// "six checks, ten seconds apart, then give up", and a test of that must not take a real minute.
    /// </param>
    public ConsoleReachabilityMonitor(
        IConsoleReachabilityProbe probe,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? restSettleInterval = null,
        int? restSettleMaxChecks = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
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

        return Task.WhenAll(cards.Select(card =>
            restRequestedHost is not null
            && string.Equals(card.Console.Host, restRequestedHost, StringComparison.OrdinalIgnoreCase)
                ? WatchRestSettleAsync(card, cancellationToken)
                : ProbeOnceAsync(card, cancellationToken)));
    }

    private async Task ProbeOnceAsync(ConsoleCardViewModel card, CancellationToken cancellationToken)
    {
        try
        {
            bool? awake = await _probe.ProbeAsync(card.Console, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            card.Reachability = Classify(awake);
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

    private static ConsoleReachability Classify(bool? awake) => awake switch
    {
        true => ConsoleReachability.Online,
        false => ConsoleReachability.Resting,
        null => ConsoleReachability.Offline,
    };
}
