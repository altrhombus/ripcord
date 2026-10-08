using Microsoft.UI.Xaml;
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

        // The router outlives every page: attached while the page is on screen, and again when it comes back from
        // the add-console page, which this page is cached across.
        Loaded += (_, _) => AttachPad();
        Unloaded += (_, _) => DetachPad();

        // A contrast theme switched on or off while the page is up repaints the step trail; see StepDashPainter.
        Loaded += (_, _) => AppEffects.Changed += OnEffectsChanged;
        Unloaded += (_, _) => AppEffects.Changed -= OnEffectsChanged;
    }

    /// <summary>The primary button: the thing each step is for.</summary>
    Control? IInitialFocusTarget.InitialFocus => PrimaryButton;

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

        WelcomePanel.Visibility = Vis(s.Step == SetupStep.Welcome);
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
        BestRadio.IsEnabled = !s.Checking && s.BestPictureAvailable;
        CompatibleRadio.IsEnabled = !s.Checking;
        _rendering = true;
        BestRadio.IsChecked = !s.Checking && s.Choice == PictureChoice.BestPicture;
        CompatibleRadio.IsChecked = !s.Checking && s.Choice == PictureChoice.MostCompatible;
        _rendering = false;
        PictureDefaultsText.Text = s.PictureDefaults;

        ConsoleIntroText.Text = s.ConsoleIntro;
        ConsolePreflightText.Text = s.ConsolePreflight;
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
        if (_focusedStep == s.Step || !IsLoaded)
        {
            return;
        }

        _focusedStep = s.Step;
        Control target = s.Step == SetupStep.Picture
            ? (s.Choice == PictureChoice.BestPicture && s.BestPictureAvailable ? BestRadio : CompatibleRadio)
            : PrimaryButton;
        DispatcherQueue.TryEnqueue(() => target.Focus(FocusState.Programmatic));
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

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
}
