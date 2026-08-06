using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Ripcord.Core.Consoles;
using Ripcord.Core.Discovery;
using Ripcord.Input;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Halyard.Consoles;
using Ripcord.Presentation.Halyard.Pairing;
using Ripcord_App.Accents;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Discovery;
using Ripcord.Protocol.Halyard.Discovery;
using Ripcord_App.Controls;
using Ripcord_App.Services;

namespace Ripcord_App.Pages;

/// <summary>
/// The add-a-console flow: pick a family, find the console on the network, enter the link code from its
/// screen, pair, and name it.
///
/// <para>
/// The registration itself — cipher availability check, <see cref="HalyardRegistrationRequest"/>,
/// <see cref="HalyardRegistrationClient"/>, storing the encrypted pairing record — is carried over unchanged
/// from the dialog this replaced. What changed is everything around it.
/// </para>
/// </summary>
public sealed partial class AddConsolePage : Page
{
    private enum Step
    {
        Family,
        Find,
        Link,
        Pairing,
        Done,
    }

    private readonly PairedConsoleStore _store = new();

    // Constructed here for now. This is one of the direct `new`s that the composition root will own once the
    // pairing flow moves onto a view-model; resolving it through a field rather than a static call is what makes
    // that a one-line change instead of an edit to PairAsync.
    private readonly IHalyardRegistrationCipherResolver _cipherResolver = new HalyardRegistrationCipherResolver();

    private readonly ObservableCollection<DiscoveredConsoleItem> _discovered = [];

    private Step _step = Step.Family;
    private ConsoleFamily _family = ConsoleFamily.Ps5;

    /// <summary>The console picked from the scan, if it came from there. Null when an address was typed.</summary>
    private DiscoveredConsoleItem? _selected;

    private string _host = string.Empty;
    private PairedConsole? _paired;

    private CancellationTokenSource? _scanCts;

    public AddConsolePage()
    {
        InitializeComponent();
        DiscoveredList.ItemsSource = _discovered;

        BuildFamilyCard(Ps5Button, ConsoleFamily.Ps5);
        BuildFamilyCard(Ps4Button, ConsoleFamily.Ps4);
        BuildFamilyCard(XboxButton, ConsoleFamily.Xbox);
        XboxButton.IsEnabled = ConsoleFamily.Xbox.IsSelectable;

        ShowStep(Step.Family);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        CancelScan();
        base.OnNavigatedFrom(e);
    }

    // ----------------------------------------------------------------------------------------------------
    // Step machinery
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Panels are shown and hidden rather than swapped through a VisualStateManager. Each carries an
    /// EntranceThemeTransition, so the step change animates — and, because XAML's own transitions consult the
    /// system animation setting themselves, it stops animating for a user who has turned effects off without
    /// this needing to ask.
    /// </summary>
    private void ShowStep(Step step)
    {
        _step = step;

        FamilyPanel.Visibility = Vis(step == Step.Family);
        FindPanel.Visibility = Vis(step == Step.Find);
        LinkPanel.Visibility = Vis(step == Step.Link);
        PairingPanel.Visibility = Vis(step == Step.Pairing);
        DonePanel.Visibility = Vis(step == Step.Done);

        // The step dashes fill as the flow advances. Pairing shares the link step's dash — it is the same
        // step from the user's point of view, just the part they are not doing anything during.
        int reached = step switch
        {
            Step.Family => 1,
            Step.Find => 2,
            Step.Link or Step.Pairing => 3,
            _ => 4,
        };
        StepDash1.Opacity = DashOpacity(1, reached);
        StepDash2.Opacity = DashOpacity(2, reached);
        StepDash3.Opacity = DashOpacity(3, reached);
        StepDash4.Opacity = DashOpacity(4, reached);

        // Enabled everywhere except during the pairing exchange, which is the one step that must not be
        // interrupted halfway — the console is mid-registration, and walking out leaves it having consumed a
        // link code for nothing. On the first step Back means "leave the flow", which is a perfectly good
        // thing to want, so it stays live rather than being a dead button on the screen you arrive at.
        BackButton.IsEnabled = step != Step.Pairing;

        switch (step)
        {
            case Step.Family:
                PrimaryButton.Visibility = Visibility.Collapsed;
                SecondaryButton.Visibility = Visibility.Collapsed;
                break;

            case Step.Find:
                PrimaryButton.Visibility = Visibility.Collapsed;
                SecondaryButton.Visibility = Visibility.Collapsed;
                break;

            case Step.Link:
                PrimaryButton.Content = "Pair";
                PrimaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Visibility = Visibility.Collapsed;
                UpdateLinkReady();
                break;

            case Step.Pairing:
                PrimaryButton.Visibility = Visibility.Collapsed;
                SecondaryButton.Visibility = Visibility.Collapsed;
                break;

            case Step.Done:
                PrimaryButton.Content = "Save & connect";
                PrimaryButton.IsEnabled = true;
                PrimaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Content = "Save";
                SecondaryButton.Visibility = Visibility.Visible;
                BackButton.IsEnabled = false; // the console is paired; going back would mean re-pairing it
                break;
        }
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private static double DashOpacity(int index, int reached) => index <= reached ? 1.0 : 0.2;

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Find:
                CancelScan();
                ShowStep(Step.Family);
                break;
            case Step.Link:
                ShowStep(Step.Find);
                StartScan();
                break;
            default:
                // Step.Family — leave the flow entirely, back to the console list.
                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
                break;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // 1. Which console
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a family's card. Done here rather than three times in markup — the three differ only in their
    /// data, and a copy each is how they drift apart.
    /// </summary>
    private static void BuildFamilyCard(Button button, ConsoleFamily family)
    {
        var panel = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };

        panel.Children.Add(new FamilyMark
        {
            Width = 16,
            Height = 28,
            Accent = AccentResources.Brush(family.Accent),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text = family.LongName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        if (family.SupportChip is { } chip)
        {
            panel.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["RipcordChipStyle"],
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = chip,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                },
            });
        }

        button.Content = panel;
    }

    private void OnFamilyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
        {
            return;
        }

        _family = ConsoleFamily.ForPlatformName(key);

        // Honest rather than aspirational: say what is not proven before the user spends time on it.
        if (_family.SupportNote is { } note)
        {
            FamilyNote.Message = note;
            FamilyNote.Severity = InfoBarSeverity.Informational;
            FamilyNote.IsOpen = true;
        }
        else
        {
            FamilyNote.IsOpen = false;
        }

        if (!_family.IsSelectable)
        {
            return;
        }

        FindHeading.Text = $"Looking for your {_family.ShortName}";
        ShowStep(Step.Find);
        StartScan();
    }

    // ----------------------------------------------------------------------------------------------------
    // 2. Find it
    // ----------------------------------------------------------------------------------------------------

    private void OnRescanClick(object sender, RoutedEventArgs e) => StartScan();

    private void OnManualEntryClick(object sender, RoutedEventArgs e)
    {
        ManualEntryPanel.Visibility = Visibility.Visible;
        ManualEntryButton.Visibility = Visibility.Collapsed;
        HostBox.Focus(FocusState.Programmatic);
    }

    private void OnUseAddressClick(object sender, RoutedEventArgs e)
    {
        string typed = HostBox.Text.Trim();
        if (typed.Length == 0)
        {
            return;
        }

        _selected = null;
        _host = typed;
        GoToLinkStep();
    }

    private void OnDiscoveredConsoleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DiscoveredConsoleItem item })
        {
            return;
        }

        // Everything the console told us about itself comes along — its family, name, host id and firmware —
        // rather than only its address, which is all the old flow carried forward.
        _selected = item;
        _family = item.Family;
        _host = item.Address;
        GoToLinkStep();
    }

    /// <summary>
    /// Scan for every family at once, whichever one the user picked.
    ///
    /// <para>
    /// Two reasons it is not limited to the chosen family. The scan used to be PS5-only in a way nobody could
    /// see — it built a search client with no profile, which silently defaults to PS5 — so choosing PS4 in the
    /// dropdown and pressing Scan could never find anything. And a user who picks the wrong one is better
    /// served by being shown their actual console than by an empty list.
    /// </para>
    /// </summary>
    private async void StartScan()
    {
        CancelScan();
        _discovered.Clear();

        var cts = new CancellationTokenSource();
        _scanCts = cts;

        ScanProgress.Visibility = Visibility.Visible;
        RescanButton.IsEnabled = false;
        FindSubheading.Text = "Make sure the console is switched on and on the same network as this PC.";

        try
        {
            await Task.WhenAll(HalyardDiscoveryProfile.All.Select(profile =>
                ScanFamilyAsync(profile, cts.Token)));
        }
        catch (Exception ex)
        {
            FindSubheading.Text = $"Couldn't search the network: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                ScanProgress.Visibility = Visibility.Collapsed;
                RescanButton.IsEnabled = true;
                DescribeScanOutcome();
            }

            cts.Dispose();
        }
    }

    private Task ScanFamilyAsync(HalyardDiscoveryProfile profile, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource();
        var service = new HalyardLanDiscoveryService(SearchWindow, profile);

        IDisposable subscription = service.Discover(cancellationToken).Subscribe(
            new AnonymousObserver<DiscoveredConsole>(
                onNext: console => DispatcherQueue.TryEnqueue(() => AddDiscovered(console)),
                // One family failing to search must not sink the other — a PS4 probe on a machine where 987
                // is already taken should still leave the PS5 results standing.
                onError: _ => completion.TrySetResult(),
                onCompleted: () => completion.TrySetResult()));

        // A disposed subscription raises neither OnCompleted nor OnError — AsyncObservable swallows the
        // cancellation — so without this a cancelled scan would leave the awaiting WhenAll hanging forever.
        CancellationTokenRegistration registration =
            cancellationToken.Register(() => completion.TrySetResult());

        return completion.Task.ContinueWith(
            _ =>
            {
                registration.Dispose();
                subscription.Dispose();
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// Long enough for a resting console, which answers more slowly than an awake one. The old flow waited a
    /// blind four seconds and showed nothing until it elapsed; this shows each console the moment it answers,
    /// so the window can be generous without feeling like one.
    /// </summary>
    private static readonly TimeSpan SearchWindow = TimeSpan.FromSeconds(4);

    private void AddDiscovered(DiscoveredConsole console)
    {
        if (_discovered.Any(d => d.Console.IpAddress.Equals(console.IpAddress)))
        {
            return;
        }

        // The family the user picked first, then everything else — their console is almost certainly the one
        // they said it was, and it should not be below a console they were not looking for.
        var item = new DiscoveredConsoleItem(console);
        int insertAt = item.Family == _family
            ? _discovered.Count(d => d.Family == _family)
            : _discovered.Count;
        _discovered.Insert(insertAt, item);
    }

    private void DescribeScanOutcome()
    {
        if (_discovered.Count == 0)
        {
            FindSubheading.Text =
                "Nothing answered. The console needs to be switched on, on the same network, and reachable — "
                + "some networks block the broadcast this uses. You can enter its address instead.";
            OnManualEntryClick(this, new RoutedEventArgs());
            return;
        }

        int otherFamily = _discovered.Count(d => d.Family != _family);
        FindSubheading.Text = otherFamily > 0
            ? "Pick your console. We also found consoles from another family — they're listed too, in case you "
              + "picked the wrong one."
            : "Pick your console.";
    }

    private void CancelScan()
    {
        _scanCts?.Cancel();
        _scanCts = null;
    }

    // ----------------------------------------------------------------------------------------------------
    // 3. Link
    // ----------------------------------------------------------------------------------------------------

    private void GoToLinkStep()
    {
        CancelScan();

        string name = _selected?.DisplayName ?? _host;
        LinkHeading.Text = $"Link this PC to {name}";

        // The menu path differs between the families, and sending someone to a menu that does not exist on
        // their console is the fastest way to lose them.
        ConsoleStepsText.Text = _family == ConsoleFamily.Ps4
            ? "Open Settings → Remote Play Connection Settings → Add Device. The console shows an 8-digit code."
            : "Open Settings → System → Remote Play → Link Device. The console shows an 8-digit code.";

        LinkStatus.IsOpen = false;
        ShowStep(Step.Link);
        PasscodeBox.Focus(FocusState.Programmatic);
    }

    private void OnLinkInputChanged(object sender, TextChangedEventArgs e) => UpdateLinkReady();

    /// <summary>
    /// Enable Pair only once both fields could plausibly be right. Borrowed from the login-passcode dialog,
    /// which does the same — a disabled button that explains itself beats a validation error after the fact.
    /// </summary>
    private void UpdateLinkReady() =>
        PrimaryButton.IsEnabled =
            PasscodeBox.Text.Trim().Length >= 8 && AccountBox.Text.Trim().Length > 0;

    // ----------------------------------------------------------------------------------------------------
    // 4. Pair
    // ----------------------------------------------------------------------------------------------------

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Link:
                await PairAsync();
                break;
            case Step.Done:
                Finish(connect: true);
                break;
        }
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        if (_step == Step.Done)
        {
            Finish(connect: false);
        }
    }

    private async Task PairAsync()
    {
        string account = AccountBox.Text.Trim();
        string passcode = PasscodeBox.Text.Trim();

        HalyardConsolePlatform platform = _family.ToHalyardPlatform();

        IHalyardRegistrationCipher cipher = _cipherResolver.Resolve(platform, out string source);
        if (!cipher.IsAvailable)
        {
            ShowLinkError($"Registration crypto unavailable: {source}");
            return;
        }

        ShowStep(Step.Pairing);
        PairingStatusText.Text = $"Registering with {_selected?.DisplayName ?? _host}…";

        try
        {
            var request = new HalyardRegistrationRequest(
                ConsoleId: _host,
                ConsoleHost: _host,
                AccountId: account,
                Passcode: passcode,
                ClientDeviceId: RandomNumberGenerator.GetBytes(32),
                Platform: platform);

            var client = new HalyardRegistrationClient(cipher);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            HalyardRegistrationResult result = await client.RegisterAsync(request, cts.Token);

            if (!result.Succeeded || result.Record is null)
            {
                ShowStep(Step.Link);
                ShowLinkError(result.FailureReason ?? "Pairing failed.");
                return;
            }

            // The console's own host-id is a better identity than its current address, which a DHCP lease can
            // move out from under us. Falls back to the address for a console typed in by hand, which is what
            // every record written before this looked like.
            string? hostId = _selected?.Console.Id;

            _paired = new PairedConsole(
                Id: string.IsNullOrEmpty(hostId) ? _host : hostId,
                Name: _family.LongName,
                Host: _host,
                Platform: _family.Key,
                // Encrypted at rest by the store's protector — this is a durable console credential.
                CredentialBlob: _store.EncodeBlob(result.Record.Serialize()))
            {
                HostId = hostId,
                ReportedName = _selected?.Console.DisplayName,
                SystemVersion = _selected?.Console.SystemVersion,
            };

            NameBox.Text = _paired.DisplayName;
            DoneSubtext.Text = $"{_paired.DisplayName} is linked to this PC. You won't need the code again.";
            ShowStep(Step.Done);
            NameBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            ShowStep(Step.Link);
            ShowLinkError($"Pairing error: {ex.Message}");
        }
    }

    private void ShowLinkError(string message)
    {
        LinkStatus.Severity = InfoBarSeverity.Error;
        LinkStatus.Message = message;
        LinkStatus.IsOpen = true;
    }

    // ----------------------------------------------------------------------------------------------------
    // 5. Done
    // ----------------------------------------------------------------------------------------------------

    private void Finish(bool connect)
    {
        if (_paired is null)
        {
            return;
        }

        // Only store a nickname if it actually differs from what the console would be called anyway —
        // otherwise a user who accepts the prefilled name silently gets it pinned, and it stops tracking the
        // console if the console is ever renamed.
        string typed = NameBox.Text.Trim();
        PairedConsole toSave = typed.Length > 0 && typed != _paired.DisplayName
            ? _paired with { Nickname = typed }
            : _paired;

        _store.Upsert(toSave);

        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }

        if (connect && App.MainWindow is MainWindow main)
        {
            main.ShowStream(toSave);
        }
    }
}
