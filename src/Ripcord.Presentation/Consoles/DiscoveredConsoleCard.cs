using Ripcord.Core.Discovery;
using Ripcord.Presentation.Resources;

namespace Ripcord.Presentation.Consoles;

/// <summary>
/// One console found on the network, as the add flow shows it.
///
/// <para>
/// Everything here comes off the discovery reply, and until this existed nearly all of it was thrown away: the
/// discovery service parsed host-id, host-name, host-type and system version, kept four of them, and the pairing
/// UI then used only the IP address — so a screen whose entire job was "which of these is yours?" answered it
/// with an unlabelled address.
/// </para>
///
/// <para>
/// A plain record rather than an <see cref="ObservableState{T}"/> view-model, because it has no mutable state:
/// a discovery result is a fact about a moment, and a console that changes is a new result rather than an
/// edit to this one. Built through <see cref="From"/> so the derived strings are computed once instead of on
/// every binding read.
/// </para>
/// </summary>
public sealed record DiscoveredConsoleCard(
    DiscoveredConsole Console,
    ConsoleFamily Family,
    string DisplayName,
    string Address,
    string Details,
    string StatusLabel,
    StatusTone StatusTone,
    AccentRole Accent,
    string AutomationName)
{
    public static DiscoveredConsoleCard From(DiscoveredConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);

        ConsoleFamily family = ConsoleFamily.ForPlatform(console.Platform);
        string address = console.IpAddress.ToString();

        // The name the console broadcasts, falling back to its address if it broadcast nothing useful.
        string displayName = string.IsNullOrWhiteSpace(console.DisplayName) ? address : console.DisplayName;

        string details = console.SystemVersion is { Length: > 0 } version
            ? $"{family.ShortName} · {address} · System {version}"
            : $"{family.ShortName} · {address}";

        // A resting console is a perfectly good thing to pair with, so this is a statement of fact rather than a
        // warning — but it is worth saying, because the console has to be awake to show a link code.
        string statusLabel = console.IsAwake ? Strings.Discovered_Ready : Strings.Discovered_InRestMode;

        return new DiscoveredConsoleCard(
            Console: console,
            Family: family,
            DisplayName: displayName,
            Address: address,
            Details: details,
            StatusLabel: statusLabel,
            StatusTone: console.IsAwake ? StatusTone.Positive : StatusTone.Caution,
            Accent: family.Accent,

            // Composed so a screen reader announces the whole card as one phrase, not four fragments.
            AutomationName: $"{displayName}, {details}, {statusLabel}");
    }
}
