using Ripcord.Core.Discovery;
using Ripcord.Core.Consoles;
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

    /// <summary>
    /// How many times to ask before believing silence.
    ///
    /// <para>
    /// One datagram was the defect. The probe sends a single SRCH and waits a second, and on hardware a
    /// console that was powered on for an entire session was reported unreachable because that one packet
    /// crossed a VLAN boundary onto a 2.4 GHz link and did not come back. UDP does not retransmit and the
    /// radio's own retry can outlast the window, so a single loss - unremarkable on that path - became a
    /// verdict.
    /// </para>
    ///
    /// <para>
    /// Retries rather than a longer wait, because the failure is a lost packet and not a slow console: three
    /// chances at a second each beats one chance at three. It costs nothing in the common case - a console
    /// that is there answers the first ask - and this is a snapshot taken when the list is built, not a poll,
    /// so even the worst case is a few seconds once per visit.
    /// </para>
    /// </summary>
    public const int DefaultSilenceRetries = 2; // 3 asks in total

    private readonly IConsoleReachabilityProbe _probe;
    private readonly Func<CancellationToken, Task<IReadOnlyCollection<DiscoveredConsole>>>? _rediscover;
    private readonly Action<PairedConsole>? _addressChanged;
    private readonly Func<CancellationToken, Task<IReadOnlyCollection<string>>>? _remotelyAvailable;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _restSettleInterval;
    private readonly int _restSettleMaxChecks;
    private readonly int _silenceRetries;

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
        int? silenceRetries = null,
        Func<CancellationToken, Task<IReadOnlyCollection<string>>>? remotelyAvailable = null,
        Func<CancellationToken, Task<IReadOnlyCollection<DiscoveredConsole>>>? rediscover = null,
        Action<PairedConsole>? addressChanged = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _remotelyAvailable = remotelyAvailable;
        _rediscover = rediscover;
        _addressChanged = addressChanged;
        _delay = delay ?? Task.Delay;
        _restSettleInterval = restSettleInterval ?? DefaultRestSettleInterval;
        _restSettleMaxChecks = restSettleMaxChecks ?? DefaultRestSettleMaxChecks;
        _silenceRetries = Math.Max(0, silenceRetries ?? DefaultSilenceRetries);
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
        // Same shape as the account list below: at most one broadcast per refresh, and only if something
        // failed to answer where we last saw it.
        var rediscovered = new Lazy<Task<IReadOnlyCollection<DiscoveredConsole>>>(
            () => _rediscover is null
                ? Task.FromResult<IReadOnlyCollection<DiscoveredConsole>>([])
                : _rediscover(cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var remote = new Lazy<Task<IReadOnlyCollection<string>>>(
            () => _remotelyAvailable is null
                ? Task.FromResult<IReadOnlyCollection<string>>([])
                : _remotelyAvailable(cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);

        return Task.WhenAll(cards.Select(card =>
            restRequestedHost is not null
            && string.Equals(card.Console.Host, restRequestedHost, StringComparison.OrdinalIgnoreCase)
                ? WatchRestSettleAsync(card, cancellationToken)
                : ProbeOnceAsync(card, rediscovered, remote, cancellationToken)));
    }

    private async Task ProbeOnceAsync(
        ConsoleCardViewModel card,
        Lazy<Task<IReadOnlyCollection<DiscoveredConsole>>> rediscovered,
        Lazy<Task<IReadOnlyCollection<string>>> remote,
        CancellationToken cancellationToken)
    {
        try
        {
            bool? awake = await AskUntilAnsweredAsync(card, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            card.Reachability = awake is null
                ? await ClassifySilenceAsync(card, rediscovered, remote, cancellationToken).ConfigureAwait(false)
                : Classify(awake);
        }
        catch (OperationCanceledException)
        {
            // The page was left or the list rebuilt: the card is gone and there is nothing to update.
        }
    }

    /// <summary>
    /// Ask the console, and keep asking while it says nothing. Returns the first real answer, or null once the
    /// retries are spent.
    ///
    /// <para>
    /// Only silence is retried. An answer of either kind is ground truth and is taken immediately, so a
    /// console that is awake, or resting, costs exactly one datagram as before.
    /// </para>
    /// </summary>
    private async Task<bool?> AskUntilAnsweredAsync(ConsoleCardViewModel card, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            bool? awake = await _probe.ProbeAsync(card.Console, cancellationToken).ConfigureAwait(false);
            if (awake is not null || attempt >= _silenceRetries || cancellationToken.IsCancellationRequested)
            {
                return awake;
            }
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
    private async Task<ConsoleReachability> ClassifySilenceAsync(
        ConsoleCardViewModel card,
        Lazy<Task<IReadOnlyCollection<DiscoveredConsole>>> rediscovered,
        Lazy<Task<IReadOnlyCollection<string>>> remote,
        CancellationToken cancellationToken)
    {
        // Before anything else: it may simply have moved. A console is normally a DHCP client, so the address
        // stored at pairing is a lease and not an identity -- and asking the address we remember is the one
        // question guaranteed to fail once that lease changes. Its host-id does not change, so a broadcast
        // finds it again wherever it landed.
        if (await RelocateAsync(card, rediscovered, cancellationToken).ConfigureAwait(false) is { } moved)
        {
            return moved;
        }

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

    /// <summary>
    /// Find a console that stopped answering where we last saw it, and follow it. Null when it was not found,
    /// so the caller carries on to the account fallback.
    ///
    /// <para>
    /// Matched on <see cref="PairedConsole.HostId"/> -- the console's own identity, which survives a lease
    /// change -- and never on the address, which is the thing being corrected. A console paired by typed
    /// address has no host-id and cannot be followed this way; that is a real limitation, and the reason
    /// pairing prefers a discovered console over a typed one.
    /// </para>
    ///
    /// <para>
    /// The new address is persisted, not merely shown. A relocation that lasted only as long as the page was
    /// open would leave every later connect using the stale address again, which is the whole bug.
    /// </para>
    /// </summary>
    private async Task<ConsoleReachability?> RelocateAsync(
        ConsoleCardViewModel card,
        Lazy<Task<IReadOnlyCollection<DiscoveredConsole>>> rediscovered,
        CancellationToken cancellationToken)
    {
        if (_rediscover is null || card.Console.HostId is not { Length: > 0 } hostId)
        {
            return null;
        }

        IReadOnlyCollection<DiscoveredConsole> found;
        try
        {
            found = await rediscovered.Value.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null; // a failed broadcast says nothing; fall through to the account
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        DiscoveredConsole? match = found.FirstOrDefault(
            c => string.Equals(c.Id, hostId, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return null;
        }

        string address = match.IpAddress.ToString();
        if (!string.Equals(address, card.Console.Host, StringComparison.OrdinalIgnoreCase))
        {
            PairedConsole moved = card.Console with { Host = address };
            card.Update(moved);
            _addressChanged?.Invoke(moved);
        }

        return match.IsAwake ? ConsoleReachability.Online : ConsoleReachability.Resting;
    }

    private static ConsoleReachability Classify(bool? awake) => awake switch
    {
        true => ConsoleReachability.Online,
        false => ConsoleReachability.Resting,
        null => ConsoleReachability.Offline,
    };
}
