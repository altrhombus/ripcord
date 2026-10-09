using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Ripcord.Core.Input;
using Ripcord.Presentation;
using Ripcord.Presentation.Setup;
using Ripcord_App.Services;

namespace Ripcord_App.Pages;

/// <summary>
/// The first-run setup's surface. What each step says, and what the buttons do, is <see cref="SetupFlow"/>'s, where
/// it is tested; this page projects its state onto the controls, opens the add-console page for the console step,
/// and tells the flow about the controller.
/// </summary>
public sealed partial class SetupPage : Page, IInitialFocusTarget, IStepBack
{
    private readonly RipcordAppServices _services;
    private SetupFlow? _flow;

    // What FocusForStep last seeded, so focus moves on a step change and not on every state change.
    private SetupStep? _focusedStep;

    // Set while Render checks a radio button, so the Checked handler doesn't send the state's own value back.
    private bool _rendering;

    // Whether a button was down in the last pad frame, so a held button is one press. The polling thread's alone.
    private bool _padHeld;

    public SetupPage()
    {
        _services = App.Services;
        InitializeComponent();

        // Choosing a picture is the step's whole question, so a choice pressed goes on: a click, Space, or a pad's
        // A, the last through FocusPilot. Continue stays for anyone who would rather look first (owner,
        // 2026-10-09). The Checked handlers have recorded the choice by the time the command runs.
        // Only from the picture step: a pad's A can arrive here twice for one press (the peer's select can click as
        // well as FocusPilot running the command), and a second Next went past the console step (owner, 2026-10-09).
        var chooseAndGoOn = new ContinueCommand(() =>
        {
            if (_flow?.State.Step == SetupStep.Picture)
            {
                _flow.Next();
            }
        });
        BestRadio.Command = chooseAndGoOn;
        CompatibleRadio.Command = chooseAndGoOn;

        // The router outlives every page: attached while the page is on screen, and again when it comes back from
        // the add-console page, which this page is cached across.
        Loaded += (_, _) => AttachPad();

        // The first render runs before the page is loaded, when FocusForStep has to wait; this is its turn, so a
        // first run opens with "Get started" focused and the mark assembling.
        Loaded += (_, _) =>
        {
            if (_flow is not null)
            {
                FocusForStep(_flow.State);
            }
        };
        Unloaded += (_, _) => DetachPad();

        // A contrast theme switched on or off while the page is up repaints the step trail; see StepDashPainter.
        Loaded += (_, _) => AppEffects.Changed += OnEffectsChanged;
        Unloaded += (_, _) => AppEffects.Changed -= OnEffectsChanged;
    }

    /// <summary>The primary button: the thing each step is for. On Welcome, its own "Get started".</summary>
    Control? IInitialFocusTarget.InitialFocus => _flow?.State.Step == SetupStep.Welcome ? WelcomeStartButton : PrimaryButton;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Back from the add-console page: the same setup, which looks for the console that was just paired.
        if (e.NavigationMode == NavigationMode.Back && _flow is not null)
        {
            _flow.ReturnedFromAddConsole();
            return;
        }

        // Anything else is a new setup, even on a cached page.
        if (_flow is not null)
        {
            _flow.PropertyChanged -= OnFlowChanged;
            _flow.Completed -= OnFlowCompleted;
        }

        _flow = _services.CreateSetupFlow(e.Parameter as SetupScope? ?? SetupScope.Full);
        _flow.PropertyChanged += OnFlowChanged;
        _flow.Completed += OnFlowCompleted;
        _focusedStep = null;
        Render(_flow.State);

        _ = _flow.StartAsync();
        _ = CheckGameInputAsync(_flow);
    }

    /// <summary>Is GameInput installed? Native work, so off the UI thread, as the About page asks it.</summary>
    private static async Task CheckGameInputAsync(SetupFlow flow)
    {
        try
        {
            flow.SetGameInputInstalled(await Task.Run(Ripcord.Input.GameInputControllerSource.IsRuntimeAvailable));
        }
        catch (Exception)
        {
            // Unknown says nothing, which is better than a warning that may be wrong.
        }
    }

    bool IStepBack.TryStepBack() => _flow?.Back() ?? false;

    private void OnFlowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_flow is not null)
        {
            Render(_flow.State);
        }
    }

    private void Render(SetupFlowState s)
    {
        TitleText.Text = s.Title;
        StepDashes.Visibility = Vis(s.ShowsDashes);
        _reachedDash = s.ReachedDash;
        PaintDashes();

        // Welcome is its own composition over the whole page; the frame steps aside for it.
        bool welcome = s.Step == SetupStep.Welcome;
        WelcomeView.Visibility = Vis(welcome);
        HeaderPanel.Visibility = Vis(!welcome);
        StepsScroller.Visibility = Vis(!welcome);
        FooterBar.Visibility = Vis(!welcome);
        WelcomeStartButton.Content = s.PrimaryLabel;
        WelcomeSkipButton.Content = s.SecondaryLabel;
        WelcomeSkipButton.Visibility = Vis(s.SecondaryLabel.Length > 0);
        PicturePanel.Visibility = Vis(s.Step == SetupStep.Picture);
        ConsolePanel.Visibility = Vis(s.Step == SetupStep.Console);
        ControllerPanel.Visibility = Vis(s.Step == SetupStep.Controller);
        DonePanel.Visibility = Vis(s.Step == SetupStep.Done);

        WelcomeBodyText.Text = s.WelcomeBody;

        PictureIntroText.Text = s.PictureIntro;
        CheckingRing.IsActive = s.Checking;
        CheckingRing.Visibility = Vis(s.Checking);
        PictureNoteText.Text = s.PictureNote;
        PictureNoteText.Visibility = Vis(s.PictureNote.Length > 0);
        BestLabelText.Text = s.BestPictureLabel;
        BestDetailText.Text = s.BestPictureDetail;
        RecommendedText.Text = s.RecommendedLabel;
        RecommendedPill.Visibility = Vis(s.BestPictureAvailable);
        CompatibleLabelText.Text = s.CompatibleLabel;
        CompatibleDetailText.Text = s.CompatibleDetail;

        // Named for a screen reader. Their content is a panel of text, which a RadioButton does not read as its
        // name, so Narrator said "radio button, 1 of 2" and nothing else (visual audit, 2026-10-08). The label is
        // the name, the pill joins it when shown, and the detail line is the help text.
        AutomationProperties.SetName(
            BestRadio, s.BestPictureAvailable ? $"{s.BestPictureLabel}, {s.RecommendedLabel}" : s.BestPictureLabel);
        AutomationProperties.SetHelpText(BestRadio, s.BestPictureDetail);
        AutomationProperties.SetName(CompatibleRadio, s.CompatibleLabel);
        AutomationProperties.SetHelpText(CompatibleRadio, s.CompatibleDetail);
        // Live during the check, so focus has somewhere to land and a quick mover is not held up; see SetupFlowState.Checking.
        // When the check finds no HEVC under a focused "Best picture", focus goes to the other choice rather than
        // falling out of the page with the control that held it.
        bool bestHadFocus = BestRadio.FocusState != FocusState.Unfocused;
        BestRadio.IsEnabled = s.Checking || s.BestPictureAvailable;
        CompatibleRadio.IsEnabled = true;
        if (bestHadFocus && !BestRadio.IsEnabled)
        {
            CompatibleRadio.Focus(FocusState.Keyboard);
        }
        _rendering = true;
        BestRadio.IsChecked = s.Choice == PictureChoice.BestPicture;
        CompatibleRadio.IsChecked = s.Choice == PictureChoice.MostCompatible;
        _rendering = false;
        PictureDefaultsText.Text = s.PictureDefaults;

        ConsoleIntroText.Text = s.ConsoleIntro;
        ConsolePreflightText.Text = s.ConsolePreflight;
        BuildPreflight(s.ConsolePreflightSteps);
        ConsoleStatusRow.Visibility = Vis(s.ConsoleAdded);
        if (s.ConsoleAdded && ConsoleStatusText.Text != s.ConsoleStatus)
        {
            Announcer.Announce(ConsoleStatusText, s.ConsoleStatus);
        }

        ConsoleStatusText.Text = s.ConsoleStatus;

        ControllerIntroText.Text = s.ControllerIntro;
        ControllerIcon.Glyph = s.ControllerSeen ? "" : "";
        if (s.Step == SetupStep.Controller && ControllerStatusText.Text != s.ControllerStatus)
        {
            Announcer.Announce(ControllerStatusText, s.ControllerStatus);
        }

        ControllerStatusText.Text = s.ControllerStatus;
        ControllerDetailText.Text = s.ControllerDetail;
        ControllerDetailText.Visibility = Vis(s.ControllerDetail.Length > 0);
        GameInputBar.Message = s.GameInputNote;
        GameInputBar.IsOpen = s.GameInputNote.Length > 0;
        ExitGestureText.Text = s.ExitGestureLine;

        DonePictureText.Text = s.DonePicture;
        DoneConsoleText.Text = s.DoneConsole;
        DoneControllerText.Text = s.DoneController;

        BackButton.Content = s.BackLabel;
        BackButton.Visibility = Vis(s.CanGoBack);
        PrimaryButton.Content = s.PrimaryLabel;
        PrimaryButton.IsEnabled = s.PrimaryEnabled;
        SecondaryButton.Content = s.SecondaryLabel;
        SecondaryButton.Visibility = Vis(s.SecondaryLabel.Length > 0);

        FocusForStep(s);
    }

    /// <summary>
    /// Focus the primary button when a step appears, so Accept on a pad or Enter does the step's main thing. On the
    /// picture step, the chosen option instead, so a pad can change it before going on.
    /// </summary>
    private void FocusForStep(SetupFlowState s)
    {
        if (_focusedStep == s.Step)
        {
            return;
        }

        // Welcome can render before the page loads, when the assembly cannot start yet; keep the lockup hidden
        // until it does rather than paint it finished and take it away again.
        if (!IsLoaded)
        {
            if (s.Step == SetupStep.Welcome && AppMotion.Enabled)
            {
                WelcomeMark.HideUntilAssembled();
                WelcomeWordmark.Opacity = 0;
            }

            return;
        }

        Control target = s.Step switch
        {
            SetupStep.Picture => s.Choice == PictureChoice.BestPicture ? BestRadio : CompatibleRadio,
            SetupStep.Welcome => WelcomeStartButton,
            _ => PrimaryButton,
        };

        // Focusing a disabled control fails and focus falls to the gear: that was the picture choices, disabled
        // during the decoder check (owner, 2026-10-09). They are live now; should a target ever be disabled, the
        // step is seeded by the render that enables it.
        if (!target.IsEnabled)
        {
            return;
        }

        _focusedStep = s.Step;

        if (s.Step == SetupStep.Welcome)
        {
            AssembleWelcome();
        }
        DispatcherQueue.TryEnqueue(() => target.Focus(FocusState.Programmatic));
    }

    /// <summary>
    /// The mark builds itself, then the word arrives beside it. Nothing moves with Windows' animation effects off.
    /// </summary>
    private void AssembleWelcome()
    {
        WelcomeMark.Assemble();

        if (!AppMotion.Enabled)
        {
            return;
        }

        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 1,
            // As the mark's wedge and first dash land, and at the pace of content arriving.
            BeginTime = AppMotion.Duration("RipcordDurationStateChange") * 2,
            Duration = new Duration(AppMotion.Duration("RipcordDurationEnter")),
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, WelcomeWordmark);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");

        WelcomeWordmark.Opacity = 0;
        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        board.Children.Add(fade);
        board.Begin();
    }

    private void OnBestChecked(object sender, RoutedEventArgs e)
    {
        if (!_rendering)
        {
            _flow?.Choose(PictureChoice.BestPicture);
        }
    }

    private void OnCompatibleChecked(object sender, RoutedEventArgs e)
    {
        if (!_rendering)
        {
            _flow?.Choose(PictureChoice.MostCompatible);
        }
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_flow is null)
        {
            return;
        }

        // The console step's main button is the add-console page itself, as the setup's step.
        if (_flow.State.Step == SetupStep.Console && !_flow.State.ConsoleAdded)
        {
            Frame.Navigate(typeof(AddConsolePage), AddConsolePage.FromSetup);
            return;
        }

        _flow.Next();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => _flow?.Secondary();

    private void OnBackClick(object sender, RoutedEventArgs e) => _flow?.Back();

    /// <summary>Finished or skipped: the consoles page, with nothing to go back to.</summary>
    private void OnFlowCompleted()
    {
        Frame.Navigate(typeof(ConsolesPage));
        Frame.BackStack.Clear();
    }

    // ---- the controller ----

    private void AttachPad()
    {
        App.Input.FrameReceived += OnPadFrame;
        App.Input.PadAttachedChanged += OnPadAttachedChanged;
        App.Input.PadFamilyChanged += OnPadFamilyChanged;
        _flow?.SetPadAttached(App.Input.PadAttached, App.Input.PadFamily);
    }

    private void DetachPad()
    {
        App.Input.FrameReceived -= OnPadFrame;
        App.Input.PadAttachedChanged -= OnPadAttachedChanged;
        App.Input.PadFamilyChanged -= OnPadFamilyChanged;
    }

    // Off the UI thread, at the pad's rate. Only a button going down is news; the flow marshals its own state.
    private void OnPadFrame(ControllerStateFrame frame)
    {
        bool held = frame.Buttons != ControllerButtons.None;
        if (held && !_padHeld)
        {
            _flow?.PadPressed(App.Input.PadFamily);
        }

        _padHeld = held;
    }

    private void OnPadAttachedChanged(bool attached) => _flow?.SetPadAttached(attached, App.Input.PadFamily);

    private void OnPadFamilyChanged(PadFamily family) => _flow?.SetPadAttached(App.Input.PadAttached, family);

    // The last step reached, kept so the trail can be repainted when the contrast theme changes under the page.
    private int _reachedDash;

    private void PaintDashes() => StepDashPainter.Paint(_reachedDash, StepDash1, StepDash2, StepDash3);

    private void OnEffectsChanged() => PaintDashes();

    /// <summary>
    /// The console-side settings as a numbered list: what to turn on, then where the console keeps it. Rebuilt only
    /// when the steps change, since the flow re-renders on every state change.
    /// </summary>
    private void BuildPreflight(IReadOnlyList<PreflightStep> steps)
    {
        if (ReferenceEquals(steps, _preflightBuilt) || (_preflightBuilt is not null && steps.SequenceEqual(_preflightBuilt)))
        {
            return;
        }

        _preflightBuilt = steps;
        ConsolePreflightList.Children.Clear();

        for (int i = 0; i < steps.Count; i++)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var number = new TextBlock
            {
                Text = (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture) + ".",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            };
            AutomationProperties.SetAccessibilityView(number, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            row.Children.Add(number);

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = steps[i].TurnOn,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
            });
            text.Children.Add(new TextBlock
            {
                Text = steps[i].Where,
                Style = (Style)Application.Current.Resources["RipcordSubtleCaptionStyle"],
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            ConsolePreflightList.Children.Add(row);
        }
    }

    private IReadOnlyList<PreflightStep>? _preflightBuilt;

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private sealed class ContinueCommand(System.Action run) : System.Windows.Input.ICommand
    {
        public event System.EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run();
    }
}
