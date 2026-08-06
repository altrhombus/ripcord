using Ripcord.Core.Consoles;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Settings;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The settings page's judgement: what is offered, what each change implies, and when a change is worth saving.
/// </summary>
public class SettingsViewModelTests
{
    /// <summary>An in-memory settings store that counts writes, since "did it save?" is half of what is at stake.</summary>
    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public RipcordSettings Current { get; private set; } = new();

        public int SaveCount { get; private set; }

        public event Action<RipcordSettings>? Changed;

        public void Save(RipcordSettings settings)
        {
            Current = settings;
            SaveCount++;
            Changed?.Invoke(settings);
        }

        public void Seed(RipcordSettings settings) => Current = settings;
    }

    private sealed class FakeCapabilities : IVideoCapabilitiesProbe
    {
        public bool Hevc { get; set; } = true;

        public bool HdrDisplay { get; set; } = true;

        public List<VideoAdapterOption> Adapters { get; } = [];

        public Exception? AdapterFault { get; set; }

        public Exception? HevcFault { get; set; }

        public int AdapterEnumerations { get; private set; }

        public bool IsHevcDecodeAvailable() => HevcFault is null ? Hevc : throw HevcFault;

        public bool IsHdrDisplayAvailable() => HdrDisplay;

        public IReadOnlyList<VideoAdapterOption> EnumerateAdapters()
        {
            AdapterEnumerations++;
            return AdapterFault is null ? Adapters : throw AdapterFault;
        }
    }

    private static (SettingsViewModel Vm, RecordingSettingsStore Store, FakeCapabilities Caps) Build(
        RipcordSettings? seed = null)
    {
        var store = new RecordingSettingsStore();
        if (seed is not null)
        {
            store.Seed(seed);
        }

        var caps = new FakeCapabilities();
        var vm = new SettingsViewModel(
            store, new InMemoryPairedConsoleStore(), caps, new ImmediateUiDispatcher());

        vm.Load();
        return (vm, store, caps);
    }

    // ---- saving on change, not on event --------------------------------------------------------

    [Fact]
    public void Setting_AValueThatIsAlreadyHeld_DoesNotSave()
    {
        // THE point of this class. The page's fourteen handlers used to each check a _loading bool because an
        // event firing did not imply anything had changed — during XAML parse, or when the page projected its
        // own state back onto its controls. Comparing values makes a duplicate event structurally a no-op.
        (SettingsViewModel vm, RecordingSettingsStore store, _) = Build();

        vm.SetAdaptiveQuality(vm.State.AdaptiveQuality);
        vm.SetBitrateMbps(vm.State.BitrateMbps);
        vm.SetResolution(vm.State.ResolutionIndex);

        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void Setting_ARealChange_SavesOnce()
    {
        (SettingsViewModel vm, RecordingSettingsStore store, _) = Build();
        bool before = vm.State.AdaptiveQuality;

        vm.SetAdaptiveQuality(!before);

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(!before, store.Current.AdaptiveQuality);
    }

    [Fact]
    public void SetResolution_MapsTheIndexToGeometry()
    {
        // The label/geometry pairing lives in exactly one place, and nothing above the view-model converts
        // between an index and a width.
        (SettingsViewModel vm, RecordingSettingsStore store, _) = Build();

        vm.SetResolution(0);

        Assert.Equal(1920, store.Current.Width);
        Assert.Equal(1080, store.Current.Height);
        Assert.Equal(60, store.Current.TargetFps);
    }

    [Fact]
    public void ResolutionIndex_ForAGeometryNotOffered_FallsBackRatherThanShowingNothing()
    {
        // A settings file edited by hand, or written by an older build, must not leave the picker blank.
        (SettingsViewModel vm, _, _) = Build(new RipcordSettings { Width = 3840, Height = 2160, TargetFps = 120 });

        Assert.Equal(2, vm.State.ResolutionIndex); // 720p60, the documented fallback
    }

    // ---- codec and HDR -------------------------------------------------------------------------

    [Fact]
    public void Codec_WithoutAnHevcDecoder_IsNotOffered()
    {
        // The codec is requested in the launchSpec at connect, so offering HEVC on a machine that cannot decode
        // it produces a stream that arrives and never displays.
        var store = new RecordingSettingsStore();
        var caps = new FakeCapabilities { Hevc = false };
        var vm = new SettingsViewModel(
            store, new InMemoryPairedConsoleStore(), caps, new ImmediateUiDispatcher());

        vm.Load();

        Assert.Single(vm.State.CodecOptions);
        Assert.False(vm.State.CodecPickerEnabled);
        Assert.False(vm.State.HdrToggleEnabled);
    }

    [Fact]
    public void Load_WithAStoredHevcRequestButNoDecoder_CorrectsTheDraft()
    {
        // Correcting the record, not merely the toggle: leaving Codec=Hevc in the draft means the next change to
        // any other setting would write back a codec this machine cannot decode.
        var store = new RecordingSettingsStore();
        store.Seed(new RipcordSettings { Codec = VideoCodec.Hevc, RequestHdr = true });

        var caps = new FakeCapabilities { Hevc = false };
        var vm = new SettingsViewModel(
            store, new InMemoryPairedConsoleStore(), caps, new ImmediateUiDispatcher());

        vm.Load();
        vm.SetAdaptiveQuality(!vm.State.AdaptiveQuality);

        Assert.Equal(VideoCodec.H264, store.Current.Codec);
        Assert.False(store.Current.RequestHdr);
    }

    [Fact]
    public void SwitchingAwayFromHevc_ClearsTheHdrRequest()
    {
        // Not merely disabled: an HDR request left set-but-disabled would silently reappear on a later switch
        // back to HEVC, which is the sort of setting that turns itself on while the user is not looking.
        (SettingsViewModel vm, RecordingSettingsStore store, _) = Build();
        vm.SetCodec(1);
        vm.SetRequestHdr(true);
        Assert.True(vm.State.RequestHdr);

        vm.SetCodec(0);

        Assert.False(vm.State.RequestHdr);
        Assert.False(store.Current.RequestHdr);
    }

    [Fact]
    public void HdrToggle_CannotBeTurnedOnOverH264()
    {
        (SettingsViewModel vm, _, _) = Build();
        vm.SetCodec(0);

        vm.SetRequestHdr(true);

        Assert.False(vm.State.RequestHdr);
    }

    [Fact]
    public void HdrChecklist_PointsAtTheOneThingThatIsMissing()
    {
        // Four prerequisites, of which the app controls one — so a user whose picture stays SDR needs to see
        // WHICH is missing rather than a sentence listing all of them.
        var store = new RecordingSettingsStore();
        var caps = new FakeCapabilities { Hevc = true, HdrDisplay = false };
        var vm = new SettingsViewModel(
            store, new InMemoryPairedConsoleStore(), caps, new ImmediateUiDispatcher());

        vm.Load();
        vm.SetCodec(1);

        HdrCheck codec = vm.State.HdrChecks[0];
        HdrCheck display = vm.State.HdrChecks[1];
        HdrCheck console = vm.State.HdrChecks[2];

        Assert.Equal(HdrCheckState.Met, codec.State);
        Assert.Equal(HdrCheckState.Unmet, display.State);
        Assert.Contains("Use HDR", display.Hint);

        // Not a cross: the console's HDR output cannot be known until a session runs, and a cross would read as
        // a fault the user could go and fix.
        Assert.Equal(HdrCheckState.Unknown, console.State);
    }

    [Fact]
    public void HdrHelp_WhenEverythingLocalIsReady_SaysSo()
    {
        (SettingsViewModel vm, _, _) = Build();
        vm.SetCodec(1);

        Assert.Contains("ready for HDR", vm.State.HdrHelp);
    }

    // ---- graphics adapters ---------------------------------------------------------------------

    [Fact]
    public void Adapters_AreNotEnumeratedUntilSomeoneAsksForASpecificGpu()
    {
        // Enumerating creates and destroys a D3D12 device per adapter to test decode capability — far too much
        // work to do on every visit to a page almost nobody uses to pin a GPU.
        (SettingsViewModel vm, _, FakeCapabilities caps) = Build();

        Assert.Equal(0, caps.AdapterEnumerations);

        vm.SetGpuPreference((int)GpuPreference.Specific);

        Assert.Equal(1, caps.AdapterEnumerations);
    }

    [Fact]
    public void Adapters_AreEnumeratedOnlyOnce()
    {
        (SettingsViewModel vm, _, FakeCapabilities caps) = Build();

        vm.SetGpuPreference((int)GpuPreference.Specific);
        vm.SetGpuPreference((int)GpuPreference.PreferPerformance);
        vm.SetGpuPreference((int)GpuPreference.Specific);

        Assert.Equal(1, caps.AdapterEnumerations);
    }

    [Fact]
    public void LeavingSpecific_DropsThePinnedAdapter()
    {
        // A stale LUID riding along in the saved record comes back if the user ever returns to Specific,
        // pointing at a GPU that may no longer be installed.
        (SettingsViewModel vm, RecordingSettingsStore store, FakeCapabilities caps) = Build();
        caps.Adapters.Add(new VideoAdapterOption("Test GPU", 0xABCD, true, true));

        vm.SetGpuPreference((int)GpuPreference.Specific);
        vm.SetAdapter(0);
        Assert.Equal(0xABCDul, store.Current.GpuLuid);

        vm.SetGpuPreference((int)GpuPreference.Auto);

        Assert.Equal(0ul, store.Current.GpuLuid);
    }

    [Fact]
    public void Adapters_ThatCannotDecode_SayWhyTheyAreAPoorChoice()
    {
        (SettingsViewModel vm, _, FakeCapabilities caps) = Build();
        caps.Adapters.Add(new VideoAdapterOption("Slow GPU", 1, SupportsHardwareDecode: false, DrivesADisplay: true));
        caps.Adapters.Add(new VideoAdapterOption("Headless GPU", 2, SupportsHardwareDecode: true, DrivesADisplay: false));

        vm.SetGpuPreference((int)GpuPreference.Specific);

        Assert.Contains("no hardware video decoding", vm.State.AdapterOptions[0]);
        Assert.Contains("adds a copy each frame", vm.State.AdapterOptions[1]);
    }

    [Fact]
    public void Adapters_WhenNoneCanDecode_Warns()
    {
        (SettingsViewModel vm, _, FakeCapabilities caps) = Build();
        caps.Adapters.Add(new VideoAdapterOption("Slow GPU", 1, SupportsHardwareDecode: false, DrivesADisplay: true));

        vm.SetGpuPreference((int)GpuPreference.Specific);

        Assert.True(vm.State.AdapterWarningVisible);
        Assert.Contains("fall back to the CPU", vm.State.AdapterWarning);
    }

    [Fact]
    public void Adapters_AreNotWarnedAboutBeforeTheyAreEnumerated()
    {
        // An empty list means "not asked yet", which is not a finding.
        (SettingsViewModel vm, _, _) = Build();

        Assert.False(vm.State.AdapterWarningVisible);
    }

    // ---- degrading rather than failing ---------------------------------------------------------

    [Fact]
    public void ACapabilityQueryThatThrows_DegradesToNotAvailable()
    {
        // This page is also where a user goes to fix a bad configuration, so it has to stay reachable when a
        // driver query fails — and "not available" is both honest and the answer that cannot promise something
        // the stream will fail to deliver.
        var store = new RecordingSettingsStore();
        var caps = new FakeCapabilities { HevcFault = new InvalidOperationException("driver said no") };
        var vm = new SettingsViewModel(
            store, new InMemoryPairedConsoleStore(), caps, new ImmediateUiDispatcher());

        vm.Load();

        Assert.Single(vm.State.CodecOptions);
        Assert.Null(vm.State.LoadError);
    }

    [Fact]
    public void AnAdapterEnumerationThatThrows_ReportsItWithoutLosingThePage()
    {
        (SettingsViewModel vm, _, FakeCapabilities caps) = Build();
        caps.AdapterFault = new InvalidOperationException("DXGI unavailable");

        vm.SetGpuPreference((int)GpuPreference.Specific);

        Assert.True(vm.State.AdapterWarningVisible);
        Assert.Contains("DXGI unavailable", vm.State.AdapterWarning);
    }

    // ---- credential banner ---------------------------------------------------------------------

    [Fact]
    public void CredentialBanner_NeverImpliesProtectionThatIsNotThere()
    {
        (SettingsViewModel vm, _, _) = Build();
        var consoles = new InMemoryPairedConsoleStore();

        // The in-memory store is by definition unencrypted, which is the case worth asserting: the banner must
        // say so rather than reassuring.
        Assert.False(consoles.CredentialsEncrypted);
        Assert.Equal(StatusTone.Caution, vm.State.CredentialTone);
        Assert.Contains("not encrypted", vm.State.CredentialTitle);
    }
}
