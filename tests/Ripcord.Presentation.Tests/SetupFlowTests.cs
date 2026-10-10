using Ripcord.Core.Consoles;
using Ripcord.Core.Input;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Settings;
using Ripcord.Presentation.Setup;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The first-run setup: who sees it, what the picture step offers and saves, and how each step ends.
/// </summary>
public class SetupFlowTests
{
    private sealed class FakeCapabilities(bool hevc, bool hdrDisplay = false) : IVideoCapabilitiesProbe
    {
        public Task<bool> IsHevcDecodeAvailableAsync() => Task.FromResult(hevc);

        public Task<bool> IsHardwareDecodeSupportedAsync() => Task.FromResult(true);

        public Task<bool> IsHdrDisplayAvailableAsync() => Task.FromResult(hdrDisplay);

        public Task<IReadOnlyList<VideoAdapterOption>> EnumerateAdaptersAsync()
            => Task.FromResult<IReadOnlyList<VideoAdapterOption>>([]);
    }

    private sealed class FaultingCapabilities : IVideoCapabilitiesProbe
    {
        public Task<bool> IsHevcDecodeAvailableAsync() => throw new InvalidOperationException("driver");

        public Task<bool> IsHardwareDecodeSupportedAsync() => throw new InvalidOperationException("driver");

        public Task<bool> IsHdrDisplayAvailableAsync() => throw new InvalidOperationException("driver");

        public Task<IReadOnlyList<VideoAdapterOption>> EnumerateAdaptersAsync() => throw new InvalidOperationException("driver");
    }

    private static PairedConsole Console(string id, string name)
        => new(id, name, "10.0.0.5", "PS5", "blob");

    private static async Task<(SetupFlow Flow, InMemorySettingsStore Settings, InMemoryPairedConsoleStore Consoles)> StartAsync(
        SetupScope scope = SetupScope.Full, bool hevc = true, bool hdrDisplay = false, IVideoCapabilitiesProbe? probe = null)
    {
        var settings = new InMemorySettingsStore();
        var consoles = new InMemoryPairedConsoleStore();
        var flow = new SetupFlow(scope, settings, consoles, probe ?? new FakeCapabilities(hevc, hdrDisplay), new ImmediateUiDispatcher());
        await flow.StartAsync();
        return (flow, settings, consoles);
    }

    // ---- who sees it ----------------------------------------------------------------------------

    [Fact]
    public void ScopeFor_AFreshInstall_IsTheWholeSetup()
        => Assert.Equal(SetupScope.Full, SetupFlow.ScopeFor(new RipcordSettings(), pairedConsoles: 0));

    [Fact]
    public void ScopeFor_AnInstallWithAConsole_IsThePictureAlone()
    {
        // From before the setup existed: the one thing it missed is the chance to choose HEVC and HDR.
        Assert.Equal(SetupScope.PictureOnly, SetupFlow.ScopeFor(new RipcordSettings(), pairedConsoles: 2));
    }

    [Fact]
    public void ScopeFor_AFinishedSetup_IsNone()
        => Assert.Null(SetupFlow.ScopeFor(new RipcordSettings { SetupVersion = SetupFlow.CurrentVersion }, 0));

    // ---- the picture ----------------------------------------------------------------------------

    [Fact]
    public async Task Picture_OnAPcWithHevc_PreselectsTheBestPicture()
    {
        (SetupFlow flow, _, _) = await StartAsync(hevc: true);

        Assert.True(flow.State.BestPictureAvailable);
        Assert.Equal(PictureChoice.BestPicture, flow.State.Choice);
        Assert.Equal(string.Empty, flow.State.PictureNote);
    }

    [Fact]
    public async Task Picture_OnAPcWithoutHevc_OffersOnlyH264_AndSaysWhy()
    {
        (SetupFlow flow, _, _) = await StartAsync(hevc: false);

        Assert.False(flow.State.BestPictureAvailable);
        Assert.Equal(PictureChoice.MostCompatible, flow.State.Choice);
        Assert.Contains("can't decode HEVC", flow.State.PictureNote, StringComparison.Ordinal);

        flow.Choose(PictureChoice.BestPicture);   // refused: there is nothing to decode it with
        Assert.Equal(PictureChoice.MostCompatible, flow.State.Choice);
    }

    [Fact]
    public async Task Picture_WhenThePcCantBeAsked_OffersOnlyWhatCertainlyWorks()
    {
        (SetupFlow flow, _, _) = await StartAsync(probe: new FaultingCapabilities());

        Assert.False(flow.State.Checking);
        Assert.Equal(PictureChoice.MostCompatible, flow.State.Choice);
    }

    // ---- the picture, while the PC is still being checked ---------------------------------------
    //
    // The choices stay live during the check, so a quick mover is never held on greyed-out options with focus
    // nowhere (owner, 2026-10-09). What they do then is kept and acted on when the check answers.

    private sealed class GatedCapabilities : IVideoCapabilitiesProbe
    {
        public TaskCompletionSource<bool> Hevc { get; } = new();

        public Task<bool> IsHevcDecodeAvailableAsync() => Hevc.Task;

        public Task<bool> IsHardwareDecodeSupportedAsync() => Task.FromResult(true);

        public Task<bool> IsHdrDisplayAvailableAsync() => Task.FromResult(false);

        public Task<IReadOnlyList<VideoAdapterOption>> EnumerateAdaptersAsync()
            => Task.FromResult<IReadOnlyList<VideoAdapterOption>>([]);
    }

    private static (SetupFlow Flow, InMemorySettingsStore Settings, GatedCapabilities Probe, Task Check) AtPictureWhileChecking()
    {
        var settings = new InMemorySettingsStore();
        var probe = new GatedCapabilities();
        var flow = new SetupFlow(SetupScope.Full, settings, new InMemoryPairedConsoleStore(), probe, new ImmediateUiDispatcher());
        Task check = flow.StartAsync();
        flow.Next();   // past Welcome
        return (flow, settings, probe, check);
    }

    [Fact]
    public void Picture_WhileChecking_OffersTheBestPicture_AndContinue()
    {
        (SetupFlow flow, _, _, _) = AtPictureWhileChecking();

        Assert.True(flow.State.Checking);
        Assert.Equal(PictureChoice.BestPicture, flow.State.Choice);
        Assert.True(flow.State.PrimaryEnabled);
    }

    [Fact]
    public async Task Picture_ContinueWhileChecking_GoesOnWhenTheCheckAnswers()
    {
        (SetupFlow flow, InMemorySettingsStore settings, GatedCapabilities probe, Task check) = AtPictureWhileChecking();

        flow.Next();
        Assert.Equal(SetupStep.Picture, flow.State.Step);

        probe.Hevc.SetResult(true);
        await check;

        Assert.Equal(SetupStep.Console, flow.State.Step);
        Assert.Equal(VideoCodec.Hevc, settings.Current.Codec);
    }

    [Fact]
    public async Task Picture_ContinueWhileChecking_StaysAndSaysWhy_WhenThePcCantDecodeHevc()
    {
        (SetupFlow flow, _, GatedCapabilities probe, Task check) = AtPictureWhileChecking();

        flow.Next();
        probe.Hevc.SetResult(false);
        await check;

        // The answer changed what was on screen, so the player sees why before anything is saved for them.
        Assert.Equal(SetupStep.Picture, flow.State.Step);
        Assert.Equal(PictureChoice.MostCompatible, flow.State.Choice);
        Assert.Contains("can't decode HEVC", flow.State.PictureNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Picture_AChoiceMadeWhileChecking_Stands()
    {
        (SetupFlow flow, InMemorySettingsStore settings, GatedCapabilities probe, Task check) = AtPictureWhileChecking();

        flow.Choose(PictureChoice.MostCompatible);
        flow.Next();
        probe.Hevc.SetResult(true);
        await check;

        Assert.Equal(SetupStep.Console, flow.State.Step);
        Assert.Equal(VideoCodec.H264, settings.Current.Codec);
    }

    [Fact]
    public async Task Picture_BackWhileChecking_TakesBackTheContinue()
    {
        (SetupFlow flow, _, GatedCapabilities probe, Task check) = AtPictureWhileChecking();

        flow.Next();
        Assert.True(flow.Back());
        probe.Hevc.SetResult(true);
        await check;

        Assert.Equal(SetupStep.Welcome, flow.State.Step);
    }

    [Theory]
    [InlineData(false, "SDR")]
    [InlineData(true, "HDR display")]
    public async Task Picture_TheBestPictureSaysWhatItMeansOnThisDisplay(bool hdrDisplay, string expected)
    {
        (SetupFlow flow, _, _) = await StartAsync(hdrDisplay: hdrDisplay);

        Assert.Contains(expected, flow.State.BestPictureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Picture_BestPicture_SavesHevcAndHdr_WhenChosen()
    {
        (SetupFlow flow, InMemorySettingsStore settings, _) = await StartAsync();
        flow.Next();   // welcome

        flow.Next();   // picture, best preselected

        Assert.Equal(VideoCodec.Hevc, settings.Current.Codec);
        Assert.True(settings.Current.RequestHdr);
        Assert.Equal(SetupStep.Console, flow.State.Step);
        Assert.Equal(0, settings.Current.SetupVersion);   // not finished yet
    }

    [Fact]
    public async Task Picture_MostCompatible_SavesH264WithoutHdr()
    {
        (SetupFlow flow, InMemorySettingsStore settings, _) = await StartAsync();
        settings.Save(settings.Current with { Codec = VideoCodec.Hevc, RequestHdr = true });
        flow.Next();

        flow.Choose(PictureChoice.MostCompatible);
        flow.Next();

        Assert.Equal(VideoCodec.H264, settings.Current.Codec);
        Assert.False(settings.Current.RequestHdr);
    }

    [Fact]
    public async Task PictureOnly_DoneSavesTheChoiceAndFinishes()
    {
        (SetupFlow flow, InMemorySettingsStore settings, _) = await StartAsync(SetupScope.PictureOnly);
        bool completed = false;
        flow.Completed += () => completed = true;

        Assert.Equal(SetupStep.Picture, flow.State.Step);
        Assert.False(flow.State.CanGoBack);
        flow.Next();

        Assert.True(completed);
        Assert.Equal(VideoCodec.Hevc, settings.Current.Codec);
        Assert.Equal(SetupFlow.CurrentVersion, settings.Current.SetupVersion);
    }

    [Fact]
    public async Task PictureOnly_KeepMySettings_ChangesNothingButStillFinishes()
    {
        (SetupFlow flow, InMemorySettingsStore settings, _) = await StartAsync(SetupScope.PictureOnly);
        bool completed = false;
        flow.Completed += () => completed = true;

        flow.Secondary();

        Assert.True(completed);
        Assert.Equal(VideoCodec.H264, settings.Current.Codec);
        Assert.Equal(SetupFlow.CurrentVersion, settings.Current.SetupVersion);
    }

    // ---- skipping and finishing -----------------------------------------------------------------

    [Fact]
    public async Task SkipSetup_KeepsTheDefaults_AndDoesNotAskAgain()
    {
        (SetupFlow flow, InMemorySettingsStore settings, _) = await StartAsync();
        bool completed = false;
        flow.Completed += () => completed = true;

        flow.Secondary();   // "Skip setup" on the welcome

        Assert.True(completed);
        Assert.Equal(VideoCodec.H264, settings.Current.Codec);
        Assert.Null(SetupFlow.ScopeFor(settings.Current, 0));
    }

    [Fact]
    public async Task TheWholeSetup_EndsOnTheConsolesAndRecordsItself()
    {
        (SetupFlow flow, InMemorySettingsStore settings, _) = await StartAsync();
        int completions = 0;
        flow.Completed += () => completions++;

        flow.Next();        // welcome
        flow.Next();        // picture
        flow.Secondary();   // console: later
        flow.Next();        // controller
        Assert.Equal(SetupStep.Done, flow.State.Step);
        flow.Next();        // done
        flow.Next();        // and a second press does nothing more

        Assert.Equal(1, completions);
        Assert.Equal(SetupFlow.CurrentVersion, settings.Current.SetupVersion);
    }

    [Fact]
    public async Task Back_WalksTheSteps_AndSaysWhenThereIsNowhereLeft()
    {
        (SetupFlow flow, _, _) = await StartAsync();
        flow.Next();
        flow.Next();

        Assert.True(flow.Back());
        Assert.Equal(SetupStep.Picture, flow.State.Step);
        Assert.True(flow.Back());
        Assert.Equal(SetupStep.Welcome, flow.State.Step);
        Assert.False(flow.Back());
    }

    // ---- the console ----------------------------------------------------------------------------

    [Fact]
    public async Task Console_ThePairedConsoleIsTheOneThatWasntThereBefore()
    {
        var settings = new InMemorySettingsStore();
        var consoles = new InMemoryPairedConsoleStore();
        consoles.Upsert(Console("old", "Living room"));
        var flow = new SetupFlow(SetupScope.Full, settings, consoles, new FakeCapabilities(true), new ImmediateUiDispatcher());
        await flow.StartAsync();
        flow.Next();
        flow.Next();
        Assert.Equal(SetupStep.Console, flow.State.Step);
        Assert.False(flow.State.ConsoleAdded);

        consoles.Upsert(Console("new", "Study"));
        flow.ReturnedFromAddConsole();

        Assert.True(flow.State.ConsoleAdded);
        Assert.Contains("Study", flow.State.ConsoleStatus, StringComparison.Ordinal);
        Assert.Equal(string.Empty, flow.State.SecondaryLabel);   // nothing to put off any more
    }

    [Fact]
    public async Task Console_ReturningWithNothingPaired_StaysAsItWas()
    {
        (SetupFlow flow, _, _) = await StartAsync();
        flow.Next();
        flow.Next();

        flow.ReturnedFromAddConsole();

        Assert.False(flow.State.ConsoleAdded);
        Assert.NotEqual(string.Empty, flow.State.SecondaryLabel);
    }

    // ---- the controller -------------------------------------------------------------------------

    [Fact]
    public async Task Controller_NoneConnected_SaysTheKeyboardWorksToo()
    {
        (SetupFlow flow, _, _) = await StartAsync();
        flow.SetPadAttached(false, PadFamily.Generic);

        Assert.False(flow.State.ControllerSeen);
        Assert.Contains("keyboard", flow.State.ControllerDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Controller_APress_NamesThePad_AndTheExitGestureInItsButtons()
    {
        (SetupFlow flow, _, _) = await StartAsync();
        flow.SetPadAttached(true, PadFamily.Generic);
        Assert.Contains("Press any button", flow.State.ControllerStatus, StringComparison.Ordinal);

        flow.PadPressed(PadFamily.Vendor);

        Assert.True(flow.State.ControllerSeen);
        Assert.Equal("PlayStation controller", flow.State.ControllerStatus);
        Assert.Contains("Options + Create + L1 + R1", flow.State.ExitGestureLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Controller_ThePadLastUsedNamesTheButtons()
    {
        // A pad's first frame can arrive before the router has noticed which engine it came from.
        (SetupFlow flow, _, _) = await StartAsync();
        flow.PadPressed(PadFamily.Generic);

        flow.SetPadAttached(true, PadFamily.Vendor);

        Assert.Equal("PlayStation controller", flow.State.ControllerStatus);
    }

    [Fact]
    public async Task Controller_NoneConnected_NamesTheExitButtonsForBothPads()
    {
        // It named the Xbox buttons alone, taught to someone who might be holding a DualSense or nothing at all
        // (visual audit, 2026-10-08).
        (SetupFlow flow, _, _) = await StartAsync();

        Assert.Contains("Options + Create + L1 + R1", flow.State.ExitGestureLine, StringComparison.Ordinal);
        Assert.Contains("Xbox", flow.State.ExitGestureLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_NoExitGesture_SaysEsc()
    {
        var settings = new InMemorySettingsStore();
        settings.Save(settings.Current with { ExitGesture = ExitGesture.None });
        var flow = new SetupFlow(
            SetupScope.Full, settings, new InMemoryPairedConsoleStore(), new FakeCapabilities(true), new ImmediateUiDispatcher());

        Assert.Contains("Esc", flow.State.ExitGestureLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Controller_GameInputMissing_SaysHowToInstallIt_AndNothingUntilAsked()
    {
        (SetupFlow flow, _, _) = await StartAsync();
        Assert.Equal(string.Empty, flow.State.GameInputNote);   // unknown says nothing

        flow.SetGameInputInstalled(false);
        Assert.Contains("GameInputRedist.msi", flow.State.GameInputNote, StringComparison.Ordinal);

        flow.SetGameInputInstalled(true);
        Assert.Equal(string.Empty, flow.State.GameInputNote);
    }

    // ---- done -----------------------------------------------------------------------------------

    [Fact]
    public async Task Done_SummarisesWhatWasChosen()
    {
        (SetupFlow flow, _, InMemoryPairedConsoleStore consoles) = await StartAsync();
        flow.Next();
        flow.Next();
        consoles.Upsert(Console("new", "Study"));
        flow.ReturnedFromAddConsole();
        flow.Next();
        flow.PadPressed(PadFamily.Generic);
        flow.Next();

        Assert.Equal(SetupStep.Done, flow.State.Step);
        Assert.Contains("HEVC with HDR", flow.State.DonePicture, StringComparison.Ordinal);
        Assert.Contains("Study", flow.State.DoneConsole, StringComparison.Ordinal);
        Assert.Contains("Xbox or other controller", flow.State.DoneController, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Done_WithNoConsole_DoesNotSayAllSet()
    {
        // "All set." over "Console: none yet" (visual audit, 2026-10-08).
        (SetupFlow flow, _, _) = await StartAsync();
        flow.Next();        // welcome
        flow.Next();        // picture
        flow.Secondary();   // console: later
        flow.Next();        // controller

        Assert.Equal(SetupStep.Done, flow.State.Step);
        Assert.DoesNotContain("All set", flow.State.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Done_WithAConsole_IsAllSet()
    {
        (SetupFlow flow, _, InMemoryPairedConsoleStore consoles) = await StartAsync();
        flow.Next();
        flow.Next();
        consoles.Upsert(Console("new", "Study"));
        flow.ReturnedFromAddConsole();
        flow.Next();
        flow.Next();

        Assert.Equal("All set.", flow.State.Title);
    }
}
