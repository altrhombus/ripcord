using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ripcord.Protocol.Halyard.Common.Discovery;
using Ripcord_App.Dialogs;
using Ripcord_App.Services;

namespace Ripcord_App.Pages;

public sealed partial class ConsolesPage : Page
{
    private readonly PairedConsoleStore _store = new();

    // Cancels the in-flight status probes when the list is rebuilt or the page goes away, so a probe cannot
    // resolve onto a row that has since been replaced.
    private CancellationTokenSource? _probeCts;

    public ConsolesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => CancelProbes();
    }

    private void Refresh()
    {
        CancelProbes();

        List<ConsoleListItem> consoles = _store.Load().Select(c => new ConsoleListItem(c)).ToList();
        ConsoleList.ItemsSource = consoles;

        bool any = consoles.Count > 0;
        ConsoleList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

        if (any)
        {
            _probeCts = new CancellationTokenSource();
            _ = ProbeStatusesAsync(consoles, _probeCts.Token);
        }
    }

    /// <summary>
    /// Probe each console's reachability once and update its row. A snapshot on open, not a poll: this runs
    /// on Refresh (page load, add, remove) and nowhere else. Probes run concurrently so one offline console's
    /// timeout does not delay the others, and each row shows "Checking…" until its own probe resolves.
    /// </summary>
    private static async Task ProbeStatusesAsync(IReadOnlyList<ConsoleListItem> consoles, CancellationToken cancellationToken)
    {
        var search = new HalyardSearchClient();

        await Task.WhenAll(consoles.Select(async item =>
        {
            // A stored host that will not parse is not something to probe — show it as offline rather than
            // throw. A blank/DNS host is not expected here (pairing stores an IP), but be defensive.
            if (!IPAddress.TryParse(item.Host, out IPAddress? address))
            {
                item.Status = ConsoleReachability.Offline;
                return;
            }

            try
            {
                var result = await search.ProbeAsync(address, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // null = no reply (offline); otherwise 200 (online) vs 620 (resting).
                item.Status = result is null
                    ? ConsoleReachability.Offline
                    : result.IsAwake ? ConsoleReachability.Online : ConsoleReachability.Resting;
            }
            catch (OperationCanceledException)
            {
                // page left / list rebuilt — the row is gone, nothing to update
            }
        }));
    }

    private void CancelProbes()
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = null;
    }

    private async void OnAddConsoleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new PairConsoleDialog { XamlRoot = XamlRoot };
            ContentDialogResult result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary && dialog.Result is { } paired)
            {
                _store.Upsert(paired);
                Refresh();
            }
        }
        catch (Exception ex)
        {
            // An event handler must not let an exception reach the dispatcher unhandled.
            await ShowErrorAsync("Couldn't add that console", ex.Message);
        }
    }

    private async void OnRemoveConsoleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string host })
        {
            return;
        }

        try
        {
            // Removing a pairing is destructive and not undoable — it discards the credential, so getting the
            // console back means re-entering a link code on the console itself. It previously happened
            // instantly on a single click, next to the Connect button.
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Remove this console?",
                Content = $"Ripcord will forget its pairing with {host}. To use it again you'll need to enter a "
                          + "new link code from the console.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                _store.Remove(host);
                Refresh();
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Couldn't remove that console", ex.Message);
        }
    }

    private void OnConnectConsoleClick(object sender, RoutedEventArgs e)
    {
        // The stream is a window-level layer above the navigation chrome, not a page inside it — see
        // MainWindow.ShowStream. SessionPage then owns a SessionController for the whole lifecycle.
        if (sender is Button { Tag: PairedConsole console } && App.MainWindow is MainWindow main)
        {
            main.ShowStream(console);
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        try
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = "OK",
            }.ShowAsync();
        }
        catch (Exception)
        {
            // Another dialog is already open; the original error is more important than reporting this.
        }
    }
}
