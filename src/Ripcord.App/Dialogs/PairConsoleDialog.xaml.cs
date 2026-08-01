using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Ripcord.Core.Discovery;
using Ripcord.Input;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Discovery;
using Ripcord_App.Services;

namespace Ripcord_App.Dialogs;

/// <summary>
/// The "add a console" pairing flow: collect the console address, PSN account id, and the on-screen link
/// code, then run <see cref="HalyardRegistrationClient"/> to obtain the pairing record. On success
/// <see cref="Result"/> holds the persistable <see cref="PairedConsole"/>.
/// </summary>
public sealed partial class PairConsoleDialog : ContentDialog
{
    private readonly List<DiscoveredConsole> _discovered = [];

    public PairConsoleDialog()
    {
        InitializeComponent();
    }

    /// <summary>Set when pairing succeeds; the caller persists it and adds it to the list.</summary>
    public PairedConsole? Result { get; private set; }

    private HalyardConsolePlatform SelectedPlatform =>
        PlatformBox.SelectedIndex == 1 ? HalyardConsolePlatform.Ps4 : HalyardConsolePlatform.Ps5;

    private async void OnScanClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        Busy.IsActive = true;
        ShowStatus(InfoBarSeverity.Informational, "Scanning the network for consoles…");
        try
        {
            _discovered.Clear();
            var service = new HalyardLanDiscoveryService();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using IDisposable sub = service.Discover(cts.Token)
                .Subscribe(new AnonymousObserver<DiscoveredConsole>(c => _discovered.Add(c)));
            try { await Task.Delay(TimeSpan.FromSeconds(4), cts.Token); } catch (TaskCanceledException) { }

            DiscoveredBox.Items.Clear();
            foreach (DiscoveredConsole c in _discovered)
                DiscoveredBox.Items.Add($"{c.DisplayName} — {c.IpAddress}{(c.IsAwake ? "" : " (standby)")}");

            if (_discovered.Count == 0)
            {
                ShowStatus(InfoBarSeverity.Warning, "No consoles responded. Enter the IP address manually.");
            }
            else
            {
                DiscoveredBox.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                ShowStatus(InfoBarSeverity.Success, $"Found {_discovered.Count} console(s). Pick one or type an IP.");
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, $"Scan failed: {ex.Message}");
        }
        finally
        {
            Busy.IsActive = false;
            ScanButton.IsEnabled = true;
        }
    }

    private void OnDiscoveredSelected(object sender, SelectionChangedEventArgs e)
    {
        int i = DiscoveredBox.SelectedIndex;
        if (i >= 0 && i < _discovered.Count)
            HostBox.Text = _discovered[i].IpAddress.ToString();
    }

    private async void OnPairClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            string host = HostBox.Text.Trim();
            string account = AccountBox.Text.Trim();
            string passcode = PasscodeBox.Text.Trim();

            if (host.Length == 0 || account.Length == 0 || passcode.Length < 8)
            {
                args.Cancel = true;
                ShowStatus(InfoBarSeverity.Warning, "Enter the console IP, PSN account id, and the 8-digit link code.");
                return;
            }

            IHalyardRegistrationCipher cipher = AppRegistrationCipher.Load(out string source);
            if (!cipher.IsAvailable)
            {
                args.Cancel = true;
                ShowStatus(InfoBarSeverity.Error, $"Registration crypto unavailable: {source}");
                return;
            }

            Busy.IsActive = true;
            ShowStatus(InfoBarSeverity.Informational, "Pairing… keep the link code on screen.");

            var request = new HalyardRegistrationRequest(
                ConsoleId: host,
                ConsoleHost: host,
                AccountId: account,
                Passcode: passcode,
                ClientDeviceId: System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
                Platform: SelectedPlatform);

            var client = new HalyardRegistrationClient(cipher);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            HalyardRegistrationResult result = await client.RegisterAsync(request, cts.Token);

            if (!result.Succeeded || result.Record is null)
            {
                args.Cancel = true;
                ShowStatus(InfoBarSeverity.Error, result.FailureReason ?? "Pairing failed.");
                return;
            }

            string name = SelectedPlatform == HalyardConsolePlatform.Ps4 ? "PlayStation 4" : "PlayStation 5";
            Result = new PairedConsole(
                Id: host,
                Name: name,
                Host: host,
                Platform: SelectedPlatform.ToString(),
                // Encrypted at rest by the store's protector — this is a durable console credential.
                CredentialBlob: new PairedConsoleStore().EncodeBlob(result.Record.Serialize()));
            // not cancelling -> the dialog closes with Primary result
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ShowStatus(InfoBarSeverity.Error, $"Pairing error: {ex.Message}");
        }
        finally
        {
            Busy.IsActive = false;
            deferral.Complete();
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
