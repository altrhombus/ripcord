using System.ComponentModel;
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
}

/// <summary>
/// A row in the console list: the persisted <see cref="PairedConsole"/> plus a live
/// <see cref="Status"/> that a background SRCH probe fills in. The record itself is immutable and carries no
/// live state, so the list needs this observable wrapper for the status dot to update after the probe
/// resolves.
///
/// <para>
/// Deliberately a snapshot, not a live feed: the status reflects the moment the page was probed, refreshed on
/// navigation, not polled. A paired-console list does not change state second to second, and continuous
/// polling would be a poor trade on a battery-powered handheld.
/// </para>
/// </summary>
public sealed class ConsoleListItem(PairedConsole console) : INotifyPropertyChanged
{
    public PairedConsole Console { get; } = console;

    public string Name => Console.Name;
    public string Host => Console.Host;

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
            // The dot's colour and its label both derive from Status, so all three change together.
            Raise(nameof(Status));
            Raise(nameof(StatusLabel));
            Raise(nameof(StatusBrush));
        }
    }

    /// <summary>
    /// A word beside the dot, never the dot alone. Colour carries the same meaning but must not be the only
    /// carrier — amber-vs-green is exactly the red/green confusion, and a bare amber dot reads as a warning
    /// rather than "connecting will wake it".
    /// </summary>
    public string StatusLabel => _status switch
    {
        ConsoleReachability.Online => "Online",
        ConsoleReachability.Resting => "Resting",
        ConsoleReachability.Offline => "Offline",
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
        _ => "TextFillColorTertiaryBrush",
    }];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
