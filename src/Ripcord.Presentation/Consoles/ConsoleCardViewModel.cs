using Ripcord.Core.Consoles;
using Ripcord.Presentation.Threading;
using Ripcord.Presentation.Resources;

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
    private CardDensity _density = CardDensity.Grid;
    private bool _pointerOver;
    private bool _pressed;

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
    /// How large this card is being drawn, and therefore how much it is allowed to say.
    ///
    /// <para>
    /// Set by the page from <see cref="CardMetrics.For"/> when the viewport or the console count changes. It
    /// lives on the card rather than being read from the page by each binding because a card has to be able
    /// to answer for itself: the front end selects a wedge width, a type role and a margin from it, and a
    /// binding that had to reach back up to the page for that would be a second path to the same fact.
    /// </para>
    /// </summary>
    public CardDensity Density
    {
        get => _density;
        set => Mutate(() => _density = value);
    }

    /// <summary>
    /// The pointer is over this card. Distinct from <see cref="IsHighlighted"/>, which is hover <em>or</em>
    /// focus and drives the card's wash.
    ///
    /// <para>
    /// Separate because the wedge responds to this and must not respond to focus: focus is the ring's job,
    /// and a second focus mark on the wedge would rebuild the confusion that splitting them removed.
    /// </para>
    /// </summary>
    public bool IsPointerOver
    {
        get => _pointerOver;
        set => Mutate(() => _pointerOver = value);
    }

    /// <summary>
    /// The card is being pressed. The primary action of the product had no pressed state at all until this
    /// existed - the wedge read unmistakably as a button and answered nothing.
    /// </summary>
    public bool IsPressed
    {
        get => _pressed;
        set => Mutate(() => _pressed = value);
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
            ConsoleReachability.Resting => Strings.Console_RestMode,
            ConsoleReachability.Offline => "Offline",

            // Not "Online": the distinction the user needs is that it is elsewhere, because that is what
            // explains the slower connect and the worse latency they are about to get.
            ConsoleReachability.Away => "Away",
            ConsoleReachability.PreparingForRest => Strings.Console_GoingToSleep,
            _ => Strings.Console_Checking,
        };

        // What activating the card does, said plainly, in the verb the player came for. "Connect" is our word
        // for a mechanism — it belongs in diagnostics, not on the thing someone presses to start playing. A
        // resting console is not a problem to solve before playing, because playing wakes it, so the label
        // promises both rather than making the user wake it first and come back.
        string actionLabel = _reachability switch
        {
            ConsoleReachability.Resting or ConsoleReachability.PreparingForRest => Strings.Console_WakeAndPlay,

            // Not "Not reachable": a console that is switched off is a normal state, and the card is already
            // dimmed. Phrased as something Ripcord could not do rather than something the console is failing at.
            ConsoleReachability.Offline => Strings.Console_CannotReach,

            // The same verb as a local console, deliberately. Connecting to a console elsewhere is the same
            // action with the same outcome; the card already says it is away, and a second hedge on the button
            // would make a working thing look conditional.
            ConsoleReachability.Away => "Play",
            _ => "Play",
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

                // **Caution, and the Positive this replaces was over-claiming.** The argument for green was
                // that nothing is wrong, which is true, and that it is ready to play, which we do not know:
                // Away means the account lists the console, not that it is awake. A console asleep in another
                // house is Away, and seen on hardware as a green dot over a console that then had to be woken
                // during the connect. Caution is the same tone Resting wears and says the same thing — it
                // will work, and it will take a moment longer.
                ConsoleReachability.Away => StatusTone.Caution,
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

            // False only when the console did not answer at all. Presentation only: it dims the action so the
            // card admits we could not reach it. It deliberately does NOT gate connecting, and the old name
            // (CanConnect) said otherwise while a front end wired it to IsEnabled.
            //
            // Silence is the only route to Offline, and silence is not knowledge -- a console powered on for a
            // whole session was reported unreachable because one datagram was lost. Blocking on it turns a lost
            // packet into a dead end, while allowing the attempt costs a failed connect that can at least say
            // what went wrong. Away is reachable -- that is the point of distinguishing it from Offline.
            IsReachable: _reachability != ConsoleReachability.Offline,
            LastConnectedLabel: LastPlayed.Describe(_console.LastConnectedUtc, _clock()),
            IsHighlighted: _highlighted,
            Density: _density,
            IsPointerOver: _pointerOver,
            IsPressed: _pressed,

            // A grid item whose content is a panel has no accessible name of its own, so without this a screen
            // reader announces a list of unlabelled tiles. Composed rather than left to the reading order, so it
            // arrives as one sentence: name, family, state, and what activating it will do.
            AutomationName: $"{_console.DisplayName}, {family.ShortName}, {statusLabel}. {actionLabel}");
    }
}
