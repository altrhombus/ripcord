using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Ripcord.Core.Discovery;

namespace Ripcord_App.Services;

/// <summary>
/// One console found on the network, as the add flow shows it.
///
/// <para>
/// Everything here comes off the SRCH reply, and until this existed nearly all of it was thrown away: the
/// discovery service parsed host-id, host-name, host-type and system version, kept four of them, and the
/// pairing UI then used only the IP address — so a screen whose entire job was "which of these is yours?"
/// answered it with an unlabelled address.
/// </para>
/// </summary>
public sealed class DiscoveredConsoleItem(DiscoveredConsole console)
{
    public DiscoveredConsole Console { get; } = console;

    public ConsoleFamily Family { get; } = ConsoleFamily.ForPlatform(console.Platform);

    /// <summary>The name the console broadcasts, falling back to its address if it broadcast nothing useful.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Console.DisplayName) ? Address : Console.DisplayName;

    public string Address => Console.IpAddress.ToString();

    public Brush AccentBrush => Family.AccentBrush;

    /// <summary>"PS5 · 10.0.0.7", or with the firmware appended when the console reported one.</summary>
    public string Details => Console.SystemVersion is { Length: > 0 } version
        ? $"{Family.ShortName} · {Address} · System {version}"
        : $"{Family.ShortName} · {Address}";

    /// <summary>
    /// A resting console is a perfectly good thing to pair with, so this is a statement of fact rather than a
    /// warning — but it is worth saying, because the console has to be awake to show a link code.
    /// </summary>
    public string StatusLabel => Console.IsAwake ? "Ready" : "In rest mode";

    public Brush StatusBrush => (Brush)Application.Current.Resources[
        Console.IsAwake ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"];

    /// <summary>Composed so a screen reader announces the whole card as one phrase, not four fragments.</summary>
    public string AutomationName => $"{DisplayName}, {Details}, {StatusLabel}";
}
