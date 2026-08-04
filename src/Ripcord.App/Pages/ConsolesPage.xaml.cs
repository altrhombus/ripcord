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

    // The console (by host) we asked to rest as we left the last session, so the next Refresh shows it
    // "Preparing for rest…" and watches it settle. Null when the last disconnect did not request rest.
    private string? _restRequestedHost;

    public ConsolesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => CancelProbes();
    }

    /// <summary>
    /// Called by the window when a stream closes and this page becomes visible again. Fixes the stale-list
    /// problem (the page's own <c>Loaded</c> only fires once, so returning from a stream never re-probed), and
    /// carries the rest-on-disconnect intent so the just-left console can be shown transitioning to rest.
    /// </summary>
    public void OnReturnedFromStream(string? consoleHost, bool restRequested)
    {
        _restRequestedHost = restRequested ? consoleHost : null;
        Refresh();
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
            // Consume the rest intent for this pass only; a later plain Refresh must not re-arm the watch.
            string? restHost = _restRequestedHost;
            _restRequestedHost = null;

            _probeCts = new CancellationTokenSource();
            _ = ProbeStatusesAsync(consoles, restHost, _probeCts.Token);
        }
    }

    // How long, and how often, to watch a rest-requested console settle. Deliberately bounded: this is not a
    // status poll, it is a one-shot transition watch that gives up after the budget so it can never become a
    // background loop. Rest/reboot is a very visible physical action, so a handful of checks is worth it.
    private static readonly TimeSpan RestSettleInterval = TimeSpan.FromSeconds(10);
    private const int RestSettleMaxChecks = 6; // 6 × 10s = 60s

    /// <summary>
    /// Probe each console's reachability once and update its row. A snapshot on open, not a poll: this runs
    /// on Refresh (page load, add, remove) and nowhere else. Probes run concurrently so one offline console's
    /// timeout does not delay the others, and each row shows "Checking…" until its own probe resolves.
    ///
    /// <para>
    /// The one exception is <paramref name="restRequestedHost"/>: the console we just asked to rest is shown
    /// "Preparing for rest…" and watched by a bounded re-check (<see cref="WatchRestSettleAsync"/>) instead of
    /// a single probe, because a console that got the rest command is still awake for a few seconds after.
    /// </para>
    /// </summary>
    private static async Task ProbeStatusesAsync(
        IReadOnlyList<ConsoleListItem> consoles, string? restRequestedHost, CancellationToken cancellationToken)
    {
        await Task.WhenAll(consoles.Select(async item =>
        {
            // A stored host that will not parse is not something to probe — show it as offline rather than
            // throw. A blank/DNS host is not expected here (pairing stores an IP), but be defensive.
            if (!IPAddress.TryParse(item.Host, out IPAddress? address))
            {
                item.Status = ConsoleReachability.Offline;
                return;
            }

            // Probe on the console's own family port/version — a PS4 answers SRCH on 987/00020020, a PS5 on
            // 9302/00030010, so a shared client would never see the other family.
            var search = new HalyardSearchClient(HalyardDiscoveryProfile.ForPlatformName(item.Console.Platform));

            try
            {
                if (restRequestedHost is not null && item.Host == restRequestedHost)
                {
                    await WatchRestSettleAsync(search, item, address, cancellationToken).ConfigureAwait(true);
                    return;
                }

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

    /// <summary>
    /// Watch a console we asked to rest until it settles. Shows "Preparing for rest…" and re-checks on a bounded
    /// schedule, ending early the moment it answers 620 (rest). If the budget runs out — the console never
    /// rested, or fully powered off and stopped answering — the row shows whatever the last probe saw. Any probe
    /// error or cancellation just stops the watch; a transition indicator is not worth surfacing failures over.
    /// </summary>
    private static async Task WatchRestSettleAsync(
        HalyardSearchClient search, ConsoleListItem item, IPAddress address, CancellationToken cancellationToken)
    {
        item.Status = ConsoleReachability.PreparingForRest;

        for (int check = 0; check < RestSettleMaxChecks; check++)
        {
            await Task.Delay(RestSettleInterval, cancellationToken).ConfigureAwait(true);

            var result = await search.ProbeAsync(address, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ConsoleReachability reach = result is null
                ? ConsoleReachability.Offline
                : result.IsAwake ? ConsoleReachability.Online : ConsoleReachability.Resting;

            // 620 is the clean end state of a rest transition — settle and stop early. Online means it hasn't
            // gone down yet, and a bare Offline can be a momentary gap mid-transition, so keep showing
            // "Preparing…" for both and let the budget decide.
            if (reach == ConsoleReachability.Resting)
            {
                item.Status = ConsoleReachability.Resting;
                return;
            }

            if (check == RestSettleMaxChecks - 1)
            {
                // Gave up: report the ground truth we last saw rather than leaving it stuck on "Preparing…".
                item.Status = reach;
            }
        }
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
