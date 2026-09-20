namespace Ripcord.Presentation.Consoles;

/// <summary>Reachability of a paired console, as a discovery probe reports it.</summary>
public enum ConsoleReachability
{
    /// <summary>Probe in flight; nothing known yet. The initial state of every row.</summary>
    Checking,

    /// <summary>Answered discovery with 200 — awake and ready to stream.</summary>
    Online,

    /// <summary>Answered discovery with 620 — in rest mode. Connecting will wake it.</summary>
    Resting,

    /// <summary>Did not answer. Powered off, off the LAN, or its stored address has changed.</summary>
    Offline,

    /// <summary>
    /// Did not answer on this network, but the account service lists it as available for remote play — so it
    /// is reachable, just not from here directly.
    ///
    /// <para>
    /// Distinct from <see cref="Offline"/> because the difference is the whole point: both look identical to a
    /// discovery probe, and treating them the same is what made a console anywhere but the user's own network
    /// unconnectable from the app while the account route could reach it perfectly well.
    /// </para>
    /// </summary>
    Away,

    /// <summary>
    /// We asked the console to rest as we disconnected and it has not settled yet. Transitional: a bounded
    /// re-check watches it until it reaches <see cref="Resting"/> or <see cref="Offline"/>.
    /// </summary>
    PreparingForRest,
}

/// <summary>
/// What a status colour <em>means</em>, never the colour itself. Each front end maps these to its own palette,
/// which on Windows means the high-contrast-aware system fills rather than literals.
/// </summary>
public enum StatusTone
{
    /// <summary>Nothing known yet.</summary>
    Unknown,

    /// <summary>Ready.</summary>
    Positive,

    /// <summary>Needs an extra step, but will work — rest mode, or settling into it.</summary>
    Caution,

    /// <summary>
    /// Not available, and that is not a fault. Offline is deliberately Neutral rather than a Critical/error
    /// tone: a powered-off console is a normal state, and colouring it like a failure cries wolf every time
    /// someone simply turns their console off.
    /// </summary>
    Neutral,
}

/// <summary>Which glyph the card's primary action shows.</summary>
public enum ActionGlyph
{
    /// <summary>Play. Also used for an unreachable console — see <see cref="ConsoleCardState.IsReachable"/>.</summary>
    Play,

    /// <summary>Power, for when connecting implies waking the console first.</summary>
    Wake,
}

/// <summary>
/// Everything a console card shows, as one immutable value.
///
/// <para>
/// Recomposed whole rather than patched property by property. The hand-rolled view-model this replaces raised
/// eleven property names from a single setter, with a comment warning that "missing one of these leaves a card
/// reading 'Offline' above a 'Connect' button", plus a second cascade of seven for a record swap. A record
/// cannot be half-updated, so that entire failure mode is gone by construction rather than by remembering to
/// extend a list.
/// </para>
///
/// <para>
/// Carries no <c>Brush</c>, no <c>Color</c> and no <c>Visibility</c>: semantic tokens and bools, which each
/// front end maps to its own types. The <c>bool</c>s are single-direction — <see cref="IsChecking"/> rather
/// than a Checking/Settled pair — because the inverse is a converter's job, not a second property's.
/// </para>
/// </summary>
public sealed record ConsoleCardState(
    string DisplayName,
    string Host,
    string Details,
    AccentRole Accent,
    ConsoleReachability Reachability,
    string StatusLabel,
    StatusTone StatusTone,
    bool IsChecking,
    string PrimaryActionLabel,
    ActionGlyph ActionGlyph,
    bool IsReachable,
    string? LastConnectedLabel,
    bool IsHighlighted,
    string AutomationName)
{
    /// <summary>True when there is a last-played caption to show at all.</summary>
    public bool HasLastConnected => LastConnectedLabel is not null;
}
