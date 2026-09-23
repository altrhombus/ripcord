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

        // Look first, ask second. The flow opens on the scan now, so this is what gets it going - from
        // Loaded rather than here, because the scan is asynchronous and results arriving before the page has
        // finished building would have nowhere to land.
        Loaded += (_, _) => _ = StartFlowAsync();

        BuildFamilyCard(Ps5Button, ConsoleFamily.Ps5);
        BuildFamilyCard(Ps4Button, ConsoleFamily.Ps4);
        BuildFamilyCard(XboxButton, ConsoleFamily.Xbox);
        // Hidden, not disabled. A greyed Xbox button tells somebody deciding whether this app is for them
        // that Xbox is supported and then that it is not, in the same breath. The column collapses with it
        // so the two that remain fill the row.
        XboxButton.Visibility = Vis(ConsoleFamily.Xbox.IsSelectable);
        XboxColumn.Width = ConsoleFamily.Xbox.IsSelectable
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

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

        // Signed in, the account ID is already known and the box comes out entirely. This is the step that used
        // to send people off to a third-party lookup tool before they could pair at all.
        // The account-id box belongs to the code route as well: signed in, the id is known, and the account
        // route does not ask for it at all.
        AccountEntryPanel.Visibility = Vis(!s.AccountIdIsAutomatic && s.CodeEntryShown);

        // Shown only when there genuinely are two routes. The app has already taken one; this is the way to
        // the other, named for what it is rather than for the mechanism behind it.
        // Composed portably, including whether it should appear at all: the flow knows whether this build
        // has an account tier and whether anybody is signed into it, and neither is this page's to judge.
        // Two offers, never both. The lead one is the step's content while the form waits to be asked for;
        // the quiet one sits by the account-id field once somebody has chosen the code route anyway. They
        // say different things because they sit in different places.
        SignInLeadPanel.Visibility = Vis(s.SignInLeads);
        SignInLeadText.Text = s.SignInLeadText;
        SignInLeadButton.Content = s.SignInActionLabel;
        UseCodeLink.Content = s.CodeRouteLabel;

        SignInInvitation.Message = s.SignInInvitation;
        SignInButton.Content = s.SignInActionLabel;
        SignInInvitation.IsOpen = s.SignInInvitation.Length > 0;

        SwitchRouteLink.Visibility = Vis(s.RouteChoiceOffered);
        SwitchRouteLink.Content = s.SwitchRouteLabel;

        ConsoleStepsCard.Visibility = Vis(s.CodeEntryShown);
        PasscodeBox.Visibility = Vis(s.CodeEntryShown);
        AccountKnownNote.Visibility = Vis(s.AccountIdIsAutomatic);
        AccountKnownNote.Message = s.AccountIdNote;
        AccountKnownNote.IsOpen = s.AccountIdIsAutomatic;

        // Success when the button below it will work, informational when it is explaining why it will not. The
        // text itself is composed portably — this page only chooses the tone, which is the layer's rule about
        // what may cross the boundary (a StatusTone, never a Brush).
        AccountPairingNote.Visibility = Vis(s.AccountPairingOffered);
        AccountPairingNote.Message = s.AccountPairingNote;
        AccountPairingNote.Severity = s.CanPairWithAccount
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Informational;
        AccountPairingNote.IsOpen = s.AccountPairingOffered;

        LinkStatus.Severity = InfoBarSeverity.Error;
        LinkStatus.Message = s.LinkError ?? string.Empty;
        LinkStatus.IsOpen = s.LinkError is not null;

        PairingStatusText.Text = s.PairingStatus;
        PairingHint.Text = s.PairingHint;
        DoneSubtext.Text = s.DoneSubtext;

        // Prefill once, and only while empty: Render runs on every state change, including the ones the user's
        // own keystrokes cause, so assigning Text unconditionally would fight their typing.
        if (s.Step == AddConsoleStep.Done && NameBox.Text.Length == 0 && s.SuggestedName.Length > 0)
        {
            NameBox.Text = s.SuggestedName;
        }

        BackButton.IsEnabled = s.CanGoBack;

        // Nothing to pair while the form is still an offer: Pair would sit there permanently disabled under
        // a proposition, which reads as a dead end rather than a choice.
        PrimaryButton.Visibility = Vis(s.Step is AddConsoleStep.Link or AddConsoleStep.Done && !s.SignInLeads);
        // Composed portably, including at Done - the label there used to be a literal in this file, which
        // put the one string the celebration turns on outside the catalogue.
        PrimaryButton.Content = s.PairActionLabel;
        PrimaryButton.IsEnabled = s.Step == AddConsoleStep.Done || s.CanPair;

        // One commit action, naming the route the step is set up for. There used to be two -- "Pair" and "Pair
        // with my account" -- above a body that described both routes at once, so nothing said which button
        // went with which half of what you had just read. The choice moved into the step; the button follows it.
        SecondaryButton.Visibility = Vis(s.Step == AddConsoleStep.Done);
        SecondaryButton.Content = "Save";
        SecondaryButton.IsEnabled = true;

        FocusForStep(s);
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private static double DashOpacity(int index, int reached) => index <= reached ? 1.0 : 0.2;

    /// <summary>
    /// Seed focus for a step that has just become visible, so a controller or keyboard always has somewhere to
    /// be. Only on a step change: re-focusing on every state change would pull the caret out of a text box while
    /// the user is typing in it.
    /// </summary>
    private void FocusForStep(AddConsoleFlowState s)
    {
        AddConsoleStep step = s.Step;

        if (_focusedStep == step)
        {
            return;
        }

        _focusedStep = step;

        // The one case where the step alone does not decide: a signed-in user whose console the account already
        // knows never types a code, so seeding the code box would put the caret in a field they will not use.
        if (step == AddConsoleStep.Link && s.CanPairWithAccount)
        {
            SecondaryButton.Focus(FocusState.Keyboard);
            return;
        }

        switch (step)
        {
            case AddConsoleStep.Family:
                Ps5Button.Focus(FocusState.Keyboard);
                break;
            case AddConsoleStep.Link:
                PasscodeBox.Focus(FocusState.Keyboard);
                break;
            case AddConsoleStep.Done:
                // Play now, NOT the name box.
                //
                // Focusing a TextBox opens the soft keyboard on a handheld, so the celebration would arrive
                // with half the screen covered by a keyboard for a field nobody has to fill in - the console
                // is already paired and already named. Renaming is a flourish somebody can reach for; the
                // thing they came for holds focus.
                PrimaryButton.Focus(FocusState.Keyboard);
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

    /// <summary>
    /// Take the other route. The one quiet way out of a decision the app made on the player's behalf.
    /// </summary>
    /// <summary>
    /// Kick the first scan.
    ///
    /// <para>
    /// async void by way of a task-returning helper, and guarded, because this is a fire-and-forget from an
    /// event handler: a scanner that throws on the way in must leave the page usable - somebody can still
    /// type an address - rather than taking the window with it.
    /// </para>
    /// </summary>
    private async Task StartFlowAsync()
    {
        try
        {
            await _flow.StartAsync();
        }
        catch (Exception)
        {
            // The flow reports a failed scan through its own subheading; there is nothing to add here, and
            // the typed-address path does not depend on the scan having worked.
        }
    }

    /// <summary>
    /// Take them to where the account lives.
    ///
    /// <para>
    /// Through the shell seam rather than by naming a page: a page must not know how another page is
    /// reached, and this lands exactly where clicking the gear would.
    /// </para>
    /// </summary>
    private void OnSignInClick(object sender, RoutedEventArgs e) => _services.Shell.ShowSettings();

    /// <summary>
    /// Show the code form. Not a fallback being grudgingly allowed - local pairing is a legitimate choice,
    /// and somebody who does not want an account connected should reach it in one press and without argument.
    /// </summary>
    private void OnUseCodeClick(object sender, RoutedEventArgs e) => _flow.RevealCodeRoute();

    private void OnSwitchRoute(object sender, RoutedEventArgs e)
        => _flow.SelectRoute(_flow.State.Route == PairingRoute.Account ? PairingRoute.Code : PairingRoute.Account);

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_flow.State.Step)
        {
            case AddConsoleStep.Link:
                await _flow.PairBySelectedRouteAsync();
                break;
            case AddConsoleStep.Done:
                _flow.Finish(NameBox.Text, connect: true);
                break;
        }
    }

    private async void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        switch (_flow.State.Step)
        {
            case AddConsoleStep.Done:
                _flow.Finish(NameBox.Text, connect: false);
                break;
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
