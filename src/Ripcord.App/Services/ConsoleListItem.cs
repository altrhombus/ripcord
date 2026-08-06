using System;
using System.ComponentModel;
using Ripcord.Core.Consoles;
using Ripcord.Presentation.Consoles;
using Ripcord_App.Accents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Ripcord_App.Services;

/// <summary>Reachability of a paired console, as a SRCH probe reports it.</summary>
public enum ConsoleReachability
{
    /// <summary>Probe in flight; nothing known yet. The initial state of every row.</summary>
    Checking,

    /// <summary>Answered SRCH with 200 — awake and ready to stream.</summary>
    Online,

    /// <summary>Answered SRCH with 620 — in rest mode. Connecting will wake it.</summary>
    Resting,

    /// <summary>Did not answer SRCH. Powered off, off the LAN, or its stored address has changed.</summary>
    Offline,

    /// <summary>We asked the console to rest as we disconnected and it has not settled yet. Transitional: a
    /// bounded re-check watches it until it reaches <see cref="Resting"/> or <see cref="Offline"/>.</summary>
    PreparingForRest,
}

/// <summary>
/// A card in the console list: the persisted <see cref="PairedConsole"/> plus a live
/// <see cref="Status"/> that a background SRCH probe fills in. The record itself is immutable and carries no
/// live state, so the list needs this observable wrapper for the status to update after the probe resolves.
///
/// <para>
/// Deliberately a snapshot, not a live feed: the status reflects the moment the page was probed, refreshed on
/// navigation, not polled. A paired-console list does not change state second to second, and continuous
/// polling would be a poor trade on a battery-powered handheld.
/// </para>
/// </summary>
public sealed class ConsoleListItem(PairedConsole console) : INotifyPropertyChanged
{
    /// <summary>
    /// The stored record. Replaced wholesale by <see cref="Update"/> rather than mutated, because
    /// <see cref="PairedConsole"/> is an immutable record — a rename produces a new one.
    /// </summary>
    public PairedConsole Console { get; private set; } = console;

    /// <summary>
    /// What the user calls this console: their nickname, else the name the console broadcasts, else the
    /// family label. See <see cref="PairedConsole.DisplayName"/>.
    /// </summary>
    public string DisplayName => Console.DisplayName;

    public string Host => Console.Host;

    /// <summary>The family this console belongs to, which is where its accent and short name come from.</summary>
    public ConsoleFamily Family => ConsoleFamily.ForPlatformName(Console.Platform);

    /// <summary>The secondary line: "PS5 · 10.0.0.7". Family first, because that is the fact being asked for.</summary>
    public string Details => $"{Family.ShortName} · {Host}";

    /// <summary>
    /// The vendor accent, for the card's mark and its wash. The family carries a portable
    /// <see cref="Ripcord.Presentation.Consoles.AccentRole"/>; turning that into something WinUI can draw is
    /// this layer's job, which is what keeps the family type linkable by a non-WinUI front end.
    /// </summary>
    public Brush AccentBrush => AccentResources.Brush(Family.Accent);

    public Windows.UI.Color AccentColor => AccentResources.Color(Family.Accent);

    private bool _highlighted;

    /// <summary>
    /// Set while the pointer is over this card or it holds focus. Drives <see cref="WashOpacity"/>, which is
    /// the card's only hover cue — the container's own background is hidden behind an opaque card, so without
    /// this a fully clickable card gives no sign that it is one.
    ///
    /// <para>
    /// Kept on the item rather than done by walking into the template: a data-template's elements have no
    /// stable identity to reach for, and the alternative — indexing into the visual tree from the event's
    /// sender — breaks silently the next time the markup is nested one level deeper.
    /// </para>
    /// </summary>
    public bool IsHighlighted
    {
        get => _highlighted;
        set
        {
            if (_highlighted == value)
            {
                return;
            }

            _highlighted = value;
            Raise(nameof(IsHighlighted));
            Raise(nameof(WashOpacity));
        }
    }

    /// <summary>
    /// How strongly the vendor accent shows through. Low enough at rest that it reads as a tint over Mica
    /// rather than a coloured panel — the same restraint the About page's identity card uses.
    /// </summary>
    public double WashOpacity => _highlighted ? 0.22 : 0.10;

    private ConsoleReachability _status = ConsoleReachability.Checking;
    public ConsoleReachability Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            // Everything the status line and the primary action show derives from Status, so it all changes
            // together. Missing one of these leaves a card reading "Offline" above a "Connect" button.
            Raise(nameof(Status));
            Raise(nameof(StatusLabel));
            Raise(nameof(StatusBrush));
            Raise(nameof(CheckingVisibility));
            Raise(nameof(SettledVisibility));
            Raise(nameof(PrimaryActionLabel));
            Raise(nameof(WakeGlyphVisibility));
            Raise(nameof(PlayGlyphVisibility));
            Raise(nameof(CanConnect));
            Raise(nameof(ActionOpacity));
            Raise(nameof(AutomationName));
        }
    }

    /// <summary>
    /// A word beside the dot, never the dot alone. Colour carries the same meaning but must not be the only
    /// carrier — amber-vs-green is exactly the red/green confusion, and a bare amber dot reads as a warning
    /// rather than "connecting will wake it".
    /// </summary>
    public string StatusLabel => _status switch
    {
        ConsoleReachability.Online => "Ready",
        ConsoleReachability.Resting => "Rest mode",
        ConsoleReachability.Offline => "Offline",
        ConsoleReachability.PreparingForRest => "Going to sleep…",
        _ => "Checking…",
    };

    /// <summary>
    /// The dot colour. Offline is a neutral grey, not a red: a powered-off console is a normal state, not an
    /// error, and colouring it like a fault would cry wolf every time the console is simply off.
    /// </summary>
    public Brush StatusBrush => (Brush)Application.Current.Resources[_status switch
    {
        ConsoleReachability.Online => "SystemFillColorSuccessBrush",
        ConsoleReachability.Resting => "SystemFillColorCautionBrush",
        ConsoleReachability.Offline => "TextFillColorDisabledBrush",
        // In transit — the same caution colour rest settles into, so the dot doesn't jump hue when it lands.
        ConsoleReachability.PreparingForRest => "SystemFillColorCautionBrush",
        _ => "TextFillColorTertiaryBrush",
    }];

    /// <summary>
    /// While the probe is in flight the dot is replaced by a small spinner. A static grey dot cannot say the
    /// difference between "we're finding out" and "we found out, and it's nothing" — which are opposite
    /// answers to the only question the card exists to answer.
    ///
    /// <para>
    /// Expressed as paired <see cref="Visibility"/> properties rather than a converter because the app has no
    /// converters and x:Bind takes these directly.
    /// </para>
    /// </summary>
    public Visibility CheckingVisibility =>
        _status == ConsoleReachability.Checking ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SettledVisibility =>
        _status == ConsoleReachability.Checking ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// What activating the card does, said plainly. A resting console is not a problem to be solved before
    /// connecting — connecting wakes it — so the label promises both rather than making the user wake it
    /// first and come back.
    /// </summary>
    public string PrimaryActionLabel => _status switch
    {
        ConsoleReachability.Resting or ConsoleReachability.PreparingForRest => "Wake & connect",
        ConsoleReachability.Offline => "Not reachable",
        _ => "Connect",
    };

    /// <summary>
    /// Which of the two glyphs the action shows: the power button when a wake is implied, otherwise play.
    /// The glyphs themselves live in the XAML, as they do everywhere else in the app — a private-use
    /// codepoint in a C# string literal is invisible in a diff and easy to corrupt.
    ///
    /// <para>
    /// Offline keeps the play glyph rather than getting an error icon of its own: the card is already dimmed
    /// and its label already says "Not reachable", and a console being switched off is not a fault.
    /// </para>
    /// </summary>
    public Visibility WakeGlyphVisibility =>
        _status is ConsoleReachability.Resting or ConsoleReachability.PreparingForRest
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PlayGlyphVisibility =>
        _status is ConsoleReachability.Resting or ConsoleReachability.PreparingForRest
            ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// False only when the console did not answer at all. Everything else is worth attempting: a console can
    /// answer 620 and still be woken, and a probe that was merely slow should not lock the user out.
    /// </summary>
    public bool CanConnect => _status != ConsoleReachability.Offline;

    /// <summary>
    /// Dims the primary action for a console that did not answer. Dimmed rather than removed: the card should
    /// still say what it is for, and an unreachable console usually just needs switching on — the affordance
    /// disappearing would read as "this console is broken".
    /// </summary>
    public double ActionOpacity => CanConnect ? 1.0 : 0.5;

    /// <summary>When this console was last streamed to, phrased for a caption. Null if it never has been.</summary>
    public string? LastConnectedLabel
    {
        get
        {
            if (Console.LastConnectedUtc is not { } last)
            {
                return null;
            }

            TimeSpan ago = DateTimeOffset.UtcNow - last;
            return ago switch
            {
                { TotalMinutes: < 2 } => "Played just now",
                { TotalMinutes: < 60 } => $"Played {(int)ago.TotalMinutes} min ago",
                { TotalHours: < 24 } => $"Played {Plural((int)ago.TotalHours, "hour")} ago",
                { TotalDays: < 7 } => $"Played {Plural((int)ago.TotalDays, "day")} ago",
                _ => $"Played {last.ToLocalTime():d MMM yyyy}",
            };
        }
    }

    public Visibility LastConnectedVisibility =>
        LastConnectedLabel is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// The card's accessible name. A GridViewItem whose content is a panel has no name of its own, so without
    /// this a screen reader announces a list of unlabelled tiles. Composed rather than left to the reading
    /// order so it arrives as one sentence — name, family, state, and what activating it will do.
    /// </summary>
    public string AutomationName => $"{DisplayName}, {Family.ShortName}, {StatusLabel}. {PrimaryActionLabel}";

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";

    /// <summary>
    /// Swap in an updated record — after a rename, or after a connect stamps the last-played time — and
    /// re-raise everything derived from it, so the card updates in place instead of the whole list being
    /// rebuilt underneath it.
    /// </summary>
    public void Update(PairedConsole updated)
    {
        Console = updated;
        Raise(nameof(Console));
        Raise(nameof(DisplayName));
        Raise(nameof(Host));
        Raise(nameof(Details));
        Raise(nameof(LastConnectedLabel));
        Raise(nameof(LastConnectedVisibility));
        Raise(nameof(AutomationName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
