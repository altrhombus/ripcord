using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Ripcord.Presentation;
using Ripcord.Presentation.Accounts;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Pairing;
using Ripcord_App.Accents;
using Ripcord_App.Controls;
using Ripcord_App.Input;
using Ripcord_App.Services;

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
public sealed partial class AddConsolePage : Page, IStepBack
{
    /// <summary>The navigation parameter that opens the page as the first-run setup's console step.</summary>
    public static readonly object FromSetup = new();

    private readonly RipcordAppServices _services;

    // Built in OnNavigatedTo, not the constructor: whether the page is the setup's console step is its navigation
    // parameter, which only arrives there. OnNavigatedTo runs before Loaded, which starts the flow.
    private AddConsoleFlow _flow = null!;

    // The step last drawn, so the celebration assembles once, on arrival, not on every state change after it.
    private AddConsoleStep? _renderedStep;

    /// <summary>
    /// A view-model of this page's own over the shared account session, used only to run a sign-in from here.
    /// The flow reads the session itself; this is the surface half.
    /// </summary>
    private readonly AccountViewModel _account;

    // Guards against a second navigation if Completed were ever raised twice.
    private bool _leaving;

    // What FocusForStep last seeded, so focus moves on a step change and not on every state change. The
    // control too: on the link step, what there is to press changes without the step changing.
    private AddConsoleStep? _focusedStep;
    private Control? _focusedTarget;

    // Keeps the caret on the same discovered console when the list reorders around it.
    private FocusAnchor? _discoveryAnchor;

    // True while focus is still where the Find step put it (Rescan, before anything was found) and the user has
    // not moved it. Only then does the first console to answer take focus; see OnDiscoveredChanged.
    private bool _awaitingFirstResult;

    public AddConsolePage()
    {
        // Resolved BEFORE InitializeComponent — see ConsolesPage. This page previously named the scanner, the
        // registrar and the store directly, which is why pairing could not be pointed at anything but the real
        // network.
        _services = App.Services;
        _account = _services.CreateAccountViewModel();

        InitializeComponent();

        // Look first, ask second. The flow opens on the scan now, so this is what gets it going - from
        // Loaded rather than here, because the scan is asynchronous and results arriving before the page has
        // finished building would have nowhere to land.
        Loaded += (_, _) => _ = StartFlowAsync();

        // A contrast theme switched on or off while the page is up repaints the step trail; see StepDashPainter.
        Loaded += (_, _) => AppEffects.Changed += OnEffectsChanged;
        Unloaded += (_, _) => AppEffects.Changed -= OnEffectsChanged;

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
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _flow = _services.CreateAddConsoleFlow(partOfSetup: ReferenceEquals(e.Parameter, FromSetup));
        _flow.PropertyChanged += (_, _) => Render(_flow.State);
        _flow.Completed += OnFlowCompleted;

        DiscoveredList.ItemsSource = _flow.Discovered;

        // The discovery list inserts sorted, so a console answering late can land ABOVE the focused row and
        // slide the caret onto a neighbour. See FocusAnchor — this is the page that motivated it.
        _discoveryAnchor = new FocusAnchor(DiscoveredList, DispatcherQueue);
        _discoveryAnchor.Watch(_flow.Discovered);

        _flow.Discovered.CollectionChanged += OnDiscoveredChanged;
        RescanButton.LostFocus += OnRescanLostFocus;

        Render(_flow.State);
    }

    /// <summary>
    /// The first console to answer takes focus, if the user is still where the page put them.
    ///
    /// <para>
    /// This page had decided the opposite: an arrival never moves focus, because a console landing under a
    /// thumb must not eat the next press. That rule is right for somebody navigating and wrong for somebody
    /// waiting. Arriving on an empty list, a pad user sat on "Search again" with their console appearing above
    /// it, and the obvious next press acted on the wrong thing (2026-10-08). So the rule now has the condition
    /// it always implied: focus moves to the first result only while it is still on the seed and has never left
    /// it. Anyone who has moved, even away and back, keeps their place; the FocusAnchor still protects them.
    /// </para>
    /// </summary>
    private void OnDiscoveredChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_awaitingFirstResult && e.Action == NotifyCollectionChangedAction.Add)
        {
            QueueHandOffToFirstResult();
        }
    }

    /// <summary>
    /// Hand focus to the first result once it can take it. Also started by the seed itself: a console can answer
    /// before the seed runs, in which case the seed found an item but nothing to focus, fell back to Rescan, and
    /// the Add that would have started this had already passed.
    ///
    /// <para>
    /// Polled briefly rather than checked once. The container exists as soon as the item does, but the button
    /// inside it is still loading and arriving under the list's add transition, and until it has loaded nothing
    /// in the container is focusable. A single look found the container and nothing to focus, every time.
    /// </para>
    /// </summary>
    private void QueueHandOffToFirstResult()
    {
        if (_handOffTimer is null)
        {
            _handOffTimer = DispatcherQueue.CreateTimer();
            _handOffTimer.Interval = TimeSpan.FromMilliseconds(50);
            _handOffTimer.IsRepeating = true;
            _handOffTimer.Tick += OnHandOffTick;
        }

        _handOffAttempts = 0;
        _handOffTimer.Start();
    }

    private void OnHandOffTick(DispatcherQueueTimer timer, object args)
    {
        // A second of waiting is far longer than a button takes to load; past it, something else is wrong and
        // focus stays where it is.
        if (TryHandOffToFirstResult() || ++_handOffAttempts >= 20)
        {
            timer.Stop();
        }
    }

    // Repeats while a hand-off waits for its button to load. One per page, stopped when the page is left.
    private DispatcherQueueTimer? _handOffTimer;
    private int _handOffAttempts;

    /// <summary>True when there is nothing more to wait for: focus was handed over, or the moment has passed.</summary>
    private bool TryHandOffToFirstResult()
    {
        if (!_awaitingFirstResult
            || XamlRoot is null
            || !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), RescanButton))
        {
            return true;
        }

        if (DiscoveredList.ContainerFromIndex(0) is not DependencyObject first
            || FocusManager.FindFirstFocusableElement(first) is not Control result)
        {
            return false;
        }

        _awaitingFirstResult = false;
        result.Focus(FocusState.Keyboard);
        return true;
    }

    private void OnRescanLostFocus(object sender, RoutedEventArgs e) => _awaitingFirstResult = false;

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

        _flow.Discovered.CollectionChanged -= OnDiscoveredChanged;
        RescanButton.LostFocus -= OnRescanLostFocus;
        _awaitingFirstResult = false;
        _handOffTimer?.Stop();

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

        _reachedDash = s.ReachedDash;
        PaintDashes();

        FamilyNote.Message = s.FamilyNote ?? string.Empty;
        FamilyNote.Severity = InfoBarSeverity.Informational;
        FamilyNote.IsOpen = s.FamilyNote is not null;

        FindHeading.Text = s.FindHeading;
        FindSubheading.Text = s.FindSubheading;
        ScanProgress.Visibility = Vis(s.IsScanning);
        // Enabled while scanning, too. It was disabled then, so a page opened mid-scan had nowhere safe to seed
        // focus and the shell's fallback put it on "Enter an address instead": a pad user pressing A to get
        // going was sent to type an IP address. Pressing it mid-scan restarts the scan, which is harmless; the
        // progress bar is what says a scan is running.
        RescanButton.IsEnabled = true;
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

        SignInErrorBar.Message = s.SignInError;
        SignInErrorBar.IsOpen = s.SignInError.Length > 0;

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
        // The note's own tone, not whether the route is available. Those agreed while the note only ever
        // said one of two things; they stop agreeing when the route is available WITH a condition, and a
        // green bar reading "turn the console on first" reads as the opposite of its own sentence.
        AccountPairingNote.Severity = s.AccountPairingNoteTone switch
        {
            StatusTone.Positive => InfoBarSeverity.Success,
            StatusTone.Caution => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        AccountPairingNote.IsOpen = s.AccountPairingOffered;

        LinkStatus.Severity = InfoBarSeverity.Error;
        if (s.LinkError is not null && LinkStatus.Message != s.LinkError)
        {
            Announcer.Announce(LinkStatus, s.LinkError, important: true);
        }

        LinkStatus.Message = s.LinkError ?? string.Empty;

        // The raw reason, small, under the plain one: for a bug report, not for reading first.
        LinkStatus.Content = s.LinkErrorDetail.Length == 0 ? null : new TextBlock
        {
            Text = s.LinkErrorDetail,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Style = (Style)Application.Current.Resources["RipcordSubtleCaptionStyle"],
        };
        LinkStatus.IsOpen = s.LinkError is not null;

        if (s.Step == AddConsoleStep.Pairing && PairingStatusText.Text != s.PairingStatus)
        {
            Announcer.Announce(PairingStatusText, s.PairingStatus);
        }

        if (s.Step == AddConsoleStep.Done && DoneSubtext.Text != s.DoneSubtext)
        {
            Announcer.Announce(DoneSubtext, s.DoneSubtext, important: true);
        }

        PairingStatusText.Text = s.PairingStatus;
        PairingHint.Text = s.PairingHint;
        DoneSubtext.Text = s.DoneSubtext;

        // Prefill once, and only while empty: Render runs on every state change, including the ones the user's
        // own keystrokes cause, so assigning Text unconditionally would fight their typing.
        if (s.Step == AddConsoleStep.Done && NameBox.Text.Length == 0 && s.SuggestedName.Length > 0)
        {
            NameBox.Text = s.SuggestedName;
        }

        BackButton.Visibility = Vis(s.HasPreviousStep);
        BackButton.IsEnabled = s.CanGoBack;

        // Nothing to pair while the form is still an offer: Pair would sit there permanently disabled under
        // a proposition, which reads as a dead end rather than a choice.
        PrimaryButton.Visibility = Vis(s.Step is AddConsoleStep.Link or AddConsoleStep.Done && !s.SignInLeads);
        // Composed portably, including at Done - the label there used to be a literal in this file, which
        // put the one string the celebration turns on outside the catalogue.
        PrimaryButton.Content = s.PairActionLabel;

        // "Play now" is a launch, so at the celebration it is the wedge (design.md; showcase plan, part 4). From
        // setup the same button goes back to setup, which launches nothing, and stays an accent button.
        bool launches = s.Step == AddConsoleStep.Done && !_flow.IsPartOfSetup;
        PrimaryButton.Style = (Style)Application.Current.Resources[launches ? "RipcordWedgeButtonStyle" : "AccentButtonStyle"];

        if (s.Step == AddConsoleStep.Done && _renderedStep != AddConsoleStep.Done)
        {
            DoneMark.Assemble();
        }

        _renderedStep = s.Step;
        PrimaryButton.IsEnabled = s.Step == AddConsoleStep.Done || s.CanPair;

        // One commit action, naming the route the step is set up for. There used to be two -- "Pair" and "Pair
        // with my account" -- above a body that described both routes at once, so nothing said which button
        // went with which half of what you had just read. The choice moved into the step; the button follows it.
        // From the setup, the one way on is back to it: the primary button says so, and there is no second.
        SecondaryButton.Visibility = Vis(s.Step == AddConsoleStep.Done && !_flow.IsPartOfSetup);
        SecondaryButton.Content = s.DoneActionLabel;
        SecondaryButton.IsEnabled = true;

        FocusForStep(s);
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // The last step reached, kept so the trail can be repainted when the contrast theme changes under the page.
    private int _reachedDash;

    private void PaintDashes() => StepDashPainter.Paint(_reachedDash, StepDash1, StepDash2, StepDash3);

    private void OnEffectsChanged() => PaintDashes();

    /// <summary>
    /// Seed focus for a step that has just become visible, so a controller or keyboard always has somewhere to
    /// be. Only on a step change: re-focusing on every state change would pull the caret out of a text box while
    /// the user is typing in it.
    /// </summary>
    private void FocusForStep(AddConsoleFlowState s)
    {
        AddConsoleStep step = s.Step;

        Control? target = step switch
        {
            // The one step the step alone does not decide. A signed-in player whose console the account knows
            // never types a code, so seeding the code box would put the caret in a field they will not use; it
            // was SecondaryButton, which this step never shows, so focus fell to Back after signing in (owner,
            // pad pass, 2026-10-09). Signed out, the offer to sign in leads, and the code box is hidden.
            AddConsoleStep.Link when s.SignInLeads => SignInLeadButton,
            AddConsoleStep.Link when s.Route == PairingRoute.Account && s.CanPairWithAccount => PrimaryButton,
            AddConsoleStep.Family => Ps5Button,
            AddConsoleStep.Link => PasscodeBox,

            // Play now, NOT the name box.
            //
            // Focusing a TextBox opens the soft keyboard on a handheld, so the celebration would arrive with
            // half the screen covered by a keyboard for a field nobody has to fill in - the console is already
            // paired and already named. Renaming is a flourish somebody can reach for; the thing they came for
            // holds focus.
            AddConsoleStep.Done => PrimaryButton,

            // The list if it already has something in it, otherwise Rescan — which is always present, so there
            // is always somewhere to land. Deliberately NOT re-run when results arrive later: a console
            // answering the broadcast while the user is reading must not pull the caret across the page, and
            // one landing under their thumb must not eat the next press. Arriving at an empty list and arrowing
            // up into it once it fills is the predictable behaviour.
            //
            // The container of an ItemsControl item is a ContentPresenter, which is not a Control; the button
            // the template draws is inside it. Testing the container itself never matched, so this always fell
            // through to Rescan even with a console on screen (2026-10-08).
            AddConsoleStep.Find => (DiscoveredList.Items.Count > 0
                    && DiscoveredList.ContainerFromIndex(0) is DependencyObject first
                    ? FocusManager.FindFirstFocusableElement(first) as Control
                    : null) ?? RescanButton,

            // Nothing to seed, and that is correct rather than an omission: the panel is a progress readout
            // with no control on it, so there is genuinely nowhere for focus to go. Focus is left on the Link
            // step's controls, which have just been collapsed — WinUI drops focus off a collapsed element, and
            // the watchdog is what puts it somewhere sane. Named here so the next reader does not add a focus
            // call to a step that has nothing to focus.
            _ => null,
        };

        if (_focusedStep == step && (step != AddConsoleStep.Link || ReferenceEquals(_focusedTarget, target)
                                     || IsTypingInto()))
        {
            return;
        }

        // Recorded only once focus has actually landed. The first render runs from OnNavigatedTo, before the
        // page is in the tree, where Focus() returns false; marking the step seeded then meant it was never
        // seeded at all, and a pad arrived on the Find step with focus nowhere (2026-10-08). The flow starts
        // on Loaded, so the next render comes promptly and tries again.
        if (target is null || target.Focus(FocusState.Keyboard))
        {
            _focusedStep = step;
            _focusedTarget = target;

            // Seeded on Rescan because nothing has answered yet: the first console that does may take focus.
            _awaitingFirstResult = step == AddConsoleStep.Find && ReferenceEquals(target, RescanButton);

            if (_awaitingFirstResult && DiscoveredList.Items.Count > 0)
            {
                QueueHandOffToFirstResult();
            }
        }
    }

    /// <summary>
    /// Whether the player has started typing into a field, which a change on the link step must not pull them
    /// out of: the account lookup landing while somebody types the code would otherwise move them to "Pair with
    /// my account" mid-code.
    /// </summary>
    private bool IsTypingInto()
        => XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) switch
        {
            TextBox box => box.Text.Length > 0,
            PasswordBox box => box.Password.Length > 0,
            _ => false,
        };

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
    /// <summary>
    /// Sign in without leaving the flow.
    ///
    /// <para>
    /// This used to navigate to the settings page, because that is where the sign-in sequence was written. The
    /// user signed in and was then standing on a settings page with pairing abandoned behind them — the way
    /// back was to start pairing over from the beginning. Sign-in is a modal over whatever asked for it, so
    /// the step the user was on is still the step they are on.
    /// </para>
    /// </summary>
    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        AccountSignInResult result = await AccountSignIn.RunAsync(_account, XamlRoot);

        // Told either way. Success starts the console-list lookup that decides whether this console even needs
        // a code; failure is a sentence on this step rather than one the user has to go somewhere to read.
        _flow.AccountSignInFinished(result.Failure);
    }

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
                // The flow ignores connect when it is the setup's step; the setup comes next.
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

    /// <summary>
    /// The shell's Back (the title bar, Esc, the pad's B) walks the flow's steps before it leaves the page, as it
    /// does in setup. It used to leave from any step, so B on the code form threw away the search the player had
    /// just waited for. During the pairing exchange it is held, as the footer's Back is.
    /// </summary>
    bool IStepBack.TryStepBack()
    {
        AddConsoleFlowState state = _flow.State;
        if (state.IsPairing)
        {
            return true;
        }

        if (!state.HasPreviousStep)
        {
            return false;
        }

        _ = _flow.BackAsync();
        return true;
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
