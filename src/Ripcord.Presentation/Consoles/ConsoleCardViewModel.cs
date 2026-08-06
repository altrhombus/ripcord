using Ripcord.Core.Consoles;
using Ripcord.Presentation.Threading;

namespace Ripcord.Presentation.Consoles;

/// <summary>
/// A card in the console list: the persisted <see cref="PairedConsole"/> plus the live reachability a background
/// probe fills in. The record itself is immutable and carries no live state, so the list needs this wrapper for
/// the card to update after a probe resolves.
///
/// <para>
/// Deliberately a snapshot, not a live feed: the status reflects the moment the page was probed, refreshed on
/// navigation, not polled. A paired-console list does not change state second to second, and continuous polling
/// would be a poor trade on a battery-powered handheld.
/// </para>
/// </summary>
public sealed class ConsoleCardViewModel : ObservableState<ConsoleCardState>
{
    private readonly Func<DateTimeOffset> _clock;

    private PairedConsole _console;
    private ConsoleReachability _reachability = ConsoleReachability.Checking;
    private bool _highlighted;

    /// <param name="clock">
    /// Injected so the last-played caption is testable at its boundaries. Same seam, and same reason, as
    /// <c>SessionController</c>'s.
    /// </param>
    public ConsoleCardViewModel(
        PairedConsole console,
        IUiDispatcher dispatcher,
        Func<DateTimeOffset>? clock = null)
        : base(dispatcher)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The stored record. Replaced wholesale by <see cref="Update"/>, never mutated.</summary>
    public PairedConsole Console => _console;

    /// <summary>The family this console belongs to, which is where its accent and short name come from.</summary>
    public ConsoleFamily Family => ConsoleFamily.ForPlatformName(_console.Platform);

    /// <summary>What a probe last reported. Setting it recomposes the whole card.</summary>
    public ConsoleReachability Reachability
    {
        get => _reachability;
        set => Mutate(() => _reachability = value);
    }

    /// <summary>
    /// Set while the pointer is over this card or it holds focus. The card's only hover cue — the container's
    /// own background sits behind an opaque card, so without this a fully clickable card gives no sign that it
    /// is one. How strongly that reads is a styling decision and lives in the front end, not here.
    /// </summary>
    public bool IsHighlighted
    {
        get => _highlighted;
        set => Mutate(() => _highlighted = value);
    }

    /// <summary>
    /// Swap in an updated record — after a rename, or after a connect stamps the last-played time — so the card
    /// updates in place instead of the whole list being rebuilt underneath it. Rebuilding would destroy focus,
    /// which matters because this grid is navigated with a gamepad.
    /// </summary>
    public void Update(PairedConsole updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        Mutate(() => _console = updated);
    }

    /// <summary>Re-derive the last-played caption, which goes stale on its own as the clock moves.</summary>
    public void RefreshElapsed() => Refresh();

    protected override ConsoleCardState Compose()
    {
        ConsoleFamily family = Family;
        bool wake = _reachability is ConsoleReachability.Resting or ConsoleReachability.PreparingForRest;

        // A word beside the dot, never the dot alone. Colour carries the same meaning but must not be the only
        // carrier — amber-vs-green is exactly the red/green confusion, and a bare amber dot reads as a warning
        // rather than "connecting will wake it".
        string statusLabel = _reachability switch
        {
            ConsoleReachability.Online => "Ready",
            ConsoleReachability.Resting => "Rest mode",
            ConsoleReachability.Offline => "Offline",
            ConsoleReachability.PreparingForRest => "Going to sleep…",
            _ => "Checking…",
        };

        // What activating the card does, said plainly. A resting console is not a problem to be solved before
        // connecting — connecting wakes it — so the label promises both rather than making the user wake it
        // first and come back.
        string actionLabel = _reachability switch
        {
            ConsoleReachability.Resting or ConsoleReachability.PreparingForRest => "Wake & connect",
            ConsoleReachability.Offline => "Not reachable",
            _ => "Connect",
        };

        return new ConsoleCardState(
            DisplayName: _console.DisplayName,
            Host: _console.Host,

            // Family first, because that is the fact being asked for.
            Details: $"{family.ShortName} · {_console.Host}",
            Accent: family.Accent,
            Reachability: _reachability,
            StatusLabel: statusLabel,
            StatusTone: _reachability switch
            {
                ConsoleReachability.Online => StatusTone.Positive,
                // In transit shares rest's tone, so the dot does not jump hue when it lands.
                ConsoleReachability.Resting or ConsoleReachability.PreparingForRest => StatusTone.Caution,
                ConsoleReachability.Offline => StatusTone.Neutral,
                _ => StatusTone.Unknown,
            },

            // While the probe is in flight the dot is replaced by a spinner. A static grey dot cannot say the
            // difference between "we're finding out" and "we found out, and it's nothing" — opposite answers to
            // the only question the card exists to answer.
            IsChecking: _reachability == ConsoleReachability.Checking,
            PrimaryActionLabel: actionLabel,

            // Offline keeps the play glyph rather than getting an error icon: the card is already dimmed and its
            // label already says "Not reachable", and a console being switched off is not a fault.
            ActionGlyph: wake ? ActionGlyph.Wake : ActionGlyph.Play,

            // False only when the console did not answer at all. Everything else is worth attempting: a console
            // can answer 620 and still be woken, and a merely slow probe should not lock the user out.
            CanConnect: _reachability != ConsoleReachability.Offline,
            LastConnectedLabel: LastPlayed.Describe(_console.LastConnectedUtc, _clock()),
            IsHighlighted: _highlighted,

            // A grid item whose content is a panel has no accessible name of its own, so without this a screen
            // reader announces a list of unlabelled tiles. Composed rather than left to the reading order, so it
            // arrives as one sentence: name, family, state, and what activating it will do.
            AutomationName: $"{_console.DisplayName}, {family.ShortName}, {statusLabel}. {actionLabel}");
    }
}
