using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Ripcord.Presentation;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Pairing;
using Ripcord_App.Accents;
using Ripcord_App.Controls;
using Ripcord_App.Input;

namespace Ripcord_App.Pages;

/// <summary>
/// The add-a-console flow, as a page rather than a dialog.
///
/// <para>
/// Presentation only. The state machine — which step, what was found, whether Pair is offered, what the console
/// said — lives in <see cref="AddConsoleFlow"/>, where it is unit-tested. This class turns one immutable
/// <see cref="AddConsoleFlowState"/> into control properties, and turns clicks back into flow calls.
/// </para>
/// </summary>
public sealed partial class AddConsolePage : Page
{
    private readonly RipcordAppServices _services;
    private readonly AddConsoleFlow _flow;

    // Guards against a second navigation if Completed were ever raised twice.
    private bool _leaving;

    // What FocusForStep last seeded, so focus moves on a step change and not on every state change.
    private AddConsoleStep? _focusedStep;

    // Keeps the caret on the same discovered console when the list reorders around it.
    private FocusAnchor? _discoveryAnchor;

    public AddConsolePage()
    {
        // Resolved BEFORE InitializeComponent — see ConsolesPage. This page previously named the scanner, the
        // registrar and the store directly, which is why pairing could not be pointed at anything but the real
        // network.
        _services = App.Services;
        _flow = _services.CreateAddConsoleFlow();

        InitializeComponent();

        _flow.PropertyChanged += (_, _) => Render(_flow.State);
        _flow.Completed += OnFlowCompleted;

        DiscoveredList.ItemsSource = _flow.Discovered;

        // The discovery list inserts sorted, so a console answering late can land ABOVE the focused row and
        // slide the caret onto a neighbour. See FocusAnchor — this is the page that motivated it.
        _discoveryAnchor = new FocusAnchor(DiscoveredList, DispatcherQueue);
        _discoveryAnchor.Watch(_flow.Discovered);

        BuildFamilyCard(Ps5Button, ConsoleFamily.Ps5);
        BuildFamilyCard(Ps4Button, ConsoleFamily.Ps4);
        BuildFamilyCard(XboxButton, ConsoleFamily.Xbox);
        XboxButton.IsEnabled = ConsoleFamily.Xbox.IsSelectable;

        Render(_flow.State);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // Cancels an in-flight scan AND an in-flight pairing. The latter is new: the page's old cancellation
        // source was only a registration timeout, so walking out mid-pairing left the exchange running and its
        // result landing on a page that no longer existed.
        _ = _flow.DisposeAsync();

        // The anchor holds handlers on the list and on the flow's collection; the flow outlives this call by
        // however long its disposal takes, so leaving it subscribed would keep answering for a dead page.
        _discoveryAnchor?.Dispose();
        _discoveryAnchor = null;

        base.OnNavigatedFrom(e);
    }

    // ----------------------------------------------------------------------------------------------------
    // Render
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Project the whole state onto the controls. One method rather than per-property handlers, because the state
    /// arrives as one value — there is no way for half of it to be applied.
    /// </summary>
    private void Render(AddConsoleFlowState s)
    {
        FamilyPanel.Visibility = Vis(s.Step == AddConsoleStep.Family);
        FindPanel.Visibility = Vis(s.Step == AddConsoleStep.Find);
        LinkPanel.Visibility = Vis(s.Step == AddConsoleStep.Link);
        PairingPanel.Visibility = Vis(s.Step == AddConsoleStep.Pairing);
        DonePanel.Visibility = Vis(s.Step == AddConsoleStep.Done);

        StepDash1.Opacity = DashOpacity(1, s.ReachedDash);
        StepDash2.Opacity = DashOpacity(2, s.ReachedDash);
        StepDash3.Opacity = DashOpacity(3, s.ReachedDash);
        StepDash4.Opacity = DashOpacity(4, s.ReachedDash);

        FamilyNote.Message = s.FamilyNote ?? string.Empty;
        FamilyNote.Severity = InfoBarSeverity.Informational;
        FamilyNote.IsOpen = s.FamilyNote is not null;

        FindHeading.Text = s.FindHeading;
        FindSubheading.Text = s.FindSubheading;
        ScanProgress.Visibility = Vis(s.IsScanning);
        RescanButton.IsEnabled = !s.IsScanning;
        ManualEntryPanel.Visibility = Vis(s.ManualEntryOpen);
        ManualEntryButton.Visibility = Vis(!s.ManualEntryOpen);

        LinkHeading.Text = s.LinkHeading;
        ConsoleStepsText.Text = s.ConsoleStepsText;
        LinkStatus.Severity = InfoBarSeverity.Error;
        LinkStatus.Message = s.LinkError ?? string.Empty;
        LinkStatus.IsOpen = s.LinkError is not null;

        PairingStatusText.Text = s.PairingStatus;
        DoneSubtext.Text = s.DoneSubtext;

        // Prefill once, and only while empty: Render runs on every state change, including the ones the user's
        // own keystrokes cause, so assigning Text unconditionally would fight their typing.
        if (s.Step == AddConsoleStep.Done && NameBox.Text.Length == 0 && s.SuggestedName.Length > 0)
        {
            NameBox.Text = s.SuggestedName;
        }

        BackButton.IsEnabled = s.CanGoBack;

        PrimaryButton.Visibility = Vis(s.Step is AddConsoleStep.Link or AddConsoleStep.Done);
        PrimaryButton.Content = s.Step == AddConsoleStep.Done ? "Save & connect" : "Pair";
        PrimaryButton.IsEnabled = s.Step == AddConsoleStep.Done || s.CanPair;

        SecondaryButton.Visibility = Vis(s.Step == AddConsoleStep.Done);
        SecondaryButton.Content = "Save";

        FocusForStep(s.Step);
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private static double DashOpacity(int index, int reached) => index <= reached ? 1.0 : 0.2;

    /// <summary>
    /// Seed focus for a step that has just become visible, so a controller or keyboard always has somewhere to
    /// be. Only on a step change: re-focusing on every state change would pull the caret out of a text box while
    /// the user is typing in it.
    /// </summary>
    private void FocusForStep(AddConsoleStep step)
    {
        if (_focusedStep == step)
        {
            return;
        }

        _focusedStep = step;

        switch (step)
        {
            case AddConsoleStep.Family:
                Ps5Button.Focus(FocusState.Keyboard);
                break;
            case AddConsoleStep.Link:
                PasscodeBox.Focus(FocusState.Keyboard);
                break;
            case AddConsoleStep.Done:
                NameBox.Focus(FocusState.Keyboard);
                break;

            case AddConsoleStep.Find:
                // The list if it already has something in it, otherwise Rescan — which is always present, so
                // there is always somewhere to land. Deliberately NOT re-run when results arrive later: a
                // console answering the broadcast while the user is reading must not pull the caret across the
                // page, and one landing under their thumb must not eat the next press. Arriving at an empty
                // list and arrowing up into it once it fills is the predictable behaviour.
                if (DiscoveredList.Items.Count > 0
                    && DiscoveredList.ContainerFromIndex(0) is Control firstResult)
                {
                    firstResult.Focus(FocusState.Keyboard);
                }
                else
                {
                    RescanButton.Focus(FocusState.Keyboard);
                }

                break;

            case AddConsoleStep.Pairing:
                // Nothing to seed, and that is correct rather than an omission: the panel is a progress
                // readout with no control on it, so there is genuinely nowhere for focus to go. Focus is left
                // on the Link step's controls, which have just been collapsed — WinUI drops focus off a
                // collapsed element, and the watchdog is what puts it somewhere sane. Named here so the next
                // reader does not add a focus call to a step that has nothing to focus.
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
                    Style = (Style)Application.Current.Resources["RipcordSubtleCaptionStyle"],
                },
            });
        }

        button.Content = panel;
    }

    private async void OnFamilyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
        {
            await _flow.SelectFamilyAsync(ConsoleFamily.ForPlatformName(key));
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // 2. Find it
    // ----------------------------------------------------------------------------------------------------

    private async void OnRescanClick(object sender, RoutedEventArgs e) => await _flow.RescanAsync();

    private void OnManualEntryClick(object sender, RoutedEventArgs e)
    {
        _flow.OpenManualEntry();
        HostBox.Focus(FocusState.Keyboard);
    }

    private void OnUseAddressClick(object sender, RoutedEventArgs e) => _flow.UseTypedAddress(HostBox.Text);

    private void OnDiscoveredConsoleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DiscoveredConsoleCard card })
        {
            _flow.SelectDiscovered(card);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // 3. Link, 4. Pair
    // ----------------------------------------------------------------------------------------------------

    private void OnLinkInputChanged(object sender, TextChangedEventArgs e)
        => _flow.SetLinkInput(PasscodeBox.Text, AccountBox.Text);

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_flow.State.Step)
        {
            case AddConsoleStep.Link:
                await _flow.PairAsync();
                break;
            case AddConsoleStep.Done:
                _flow.Finish(NameBox.Text, connect: true);
                break;
        }
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        if (_flow.State.Step == AddConsoleStep.Done)
        {
            _flow.Finish(NameBox.Text, connect: false);
        }
    }

    private async void OnBackClick(object sender, RoutedEventArgs e)
    {
        // False means the flow had nowhere left to step back to, so back means leaving it.
        if (!await _flow.BackAsync() && Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // 5. Done
    // ----------------------------------------------------------------------------------------------------

    private void OnFlowCompleted(AddConsoleCompletion completion)
    {
        if (_leaving)
        {
            return;
        }

        _leaving = true;

        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }

        if (completion.ConnectNow)
        {
            _services.Shell.ShowStream(completion.Console);
        }
    }
}
