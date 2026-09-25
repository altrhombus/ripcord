using Ripcord.Core.Consoles;
using Ripcord.Core.Input;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Threading;
using Ripcord.Presentation.Resources;

namespace Ripcord.Presentation.Settings;

/// <summary>
/// The settings page's state and its save policy.
///
/// <para>
/// <b>The <c>_loading</c> flag is gone, and this is why.</b> The page used to carry a bool that every one of its
/// fourteen change handlers checked, because XAML parsing raises change events on its own — setting
/// <c>Slider.Minimum="2"</c> during <c>InitializeComponent</c> moves <c>Value</c> from 0 to 2 and fires
/// <c>ValueChanged</c> before the controls declared later in the file exist. That produced two distinct faults:
/// a save that dereferenced nulls, and a save that misrepresented what the user wanted.
/// </para>
///
/// <para>
/// The first disappears here because saving no longer reads controls — the draft record is the truth, and it is
/// complete from the moment it is loaded. The second is answered by <b>saving on change rather than on event</b>:
/// every setter below compares against what is already held and does nothing when they agree. A duplicate event
/// is then structurally a no-op, rather than something a flag has to be remembered for.
/// </para>
///
/// <para>
/// That same property is what stops the render loop. A front end projects this state onto its controls, and any
/// change event that projection provokes necessarily reports a value this view-model already holds — so it
/// terminates on the first pass without anyone having to suppress anything.
/// </para>
/// </summary>
public sealed class SettingsViewModel : ObservableState<SettingsViewState>
{
    private readonly ISettingsStore _store;
    private readonly IPairedConsoleStore _consoles;
    private readonly IVideoCapabilitiesProbe _capabilities;

    private RipcordSettings _draft = new();

    // Probed once and remembered: each is a native query, and two of them are cheap only by comparison.
    private bool _hevcAvailable;
    private bool _displayHdr;

    // Enumerated lazily — see EnsureAdapters. Building this list creates and destroys a D3D12 device per adapter
    // to test decode capability, which is far too much work to do on every visit to a page almost nobody uses to
    // pin a GPU.
    private IReadOnlyList<VideoAdapterOption>? _adapters;

    private string? _loadError;
    private string? _adapterError;

    /// <summary>
    /// The resolutions offered, and the only place the label/geometry pairing lives. A user picks a row; nothing
    /// above this type ever converts between an index and a width.
    /// </summary>
    private static readonly (int Width, int Height, int Fps)[] Resolutions =
    [
        (1920, 1080, 60),
        (1920, 1080, 30),
        (1280, 720, 60),
        (1280, 720, 30),
        (960, 540, 60),
    ];

    /// <summary>
    /// Labels for <see cref="Resolutions"/>, in the same index order.
    ///
    /// <para>A property rather than a static array on purpose: a static initialiser would resolve the
    /// catalogue once, against whichever culture happened to be current when this type was first touched,
    /// and keep that for the life of the process. Every option table below is a property for the same
    /// reason.</para>
    /// </summary>
    private static string[] ResolutionLabels =>
    [
        Strings.Settings_Resolution1080p60,
        Strings.Settings_Resolution1080p30,
        Strings.Settings_Resolution720p60,
        Strings.Settings_Resolution720p30,
        Strings.Settings_Resolution540p60,
    ];

    /// <summary>Index of the 720p60 entry, used when the stored geometry matches no offered row.</summary>
    private const int DefaultResolutionIndex = 2;

    private static string H264Label => Strings.Settings_CodecH264;

    private static string HevcLabel => Strings.Settings_CodecHevc;

    private static string[] UpscaleOptions => [Strings.Settings_UpscaleSmooth, Strings.Settings_UpscaleSharp];

    private static string[] GpuOptions =>
    [
        Strings.Settings_GpuAutomatic,
        Strings.Settings_GpuBatteryLife,
        Strings.Settings_GpuPerformance,
        Strings.Settings_GpuSpecific,
    ];

    private static string[] ExitGestureOptions =>
    [
        Strings.Settings_ExitGestureShoulders,
        Strings.Settings_ExitGestureSticks,
        Strings.Settings_ExitGestureKeyboard,
    ];

    public SettingsViewModel(
        ISettingsStore store,
        IPairedConsoleStore consoles,
        IVideoCapabilitiesProbe capabilities,
        IUiDispatcher dispatcher)
        : base(dispatcher)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _consoles = consoles ?? throw new ArgumentNullException(nameof(consoles));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    /// <summary>The labels a front end needs to fill its pickers, in index order.</summary>
    public static IReadOnlyList<string> UpscaleLabels => UpscaleOptions;

    public static IReadOnlyList<string> GpuLabels => GpuOptions;

    public static IReadOnlyList<string> ExitGestureLabels => ExitGestureOptions;

    /// <summary>
    /// Read the stored settings and probe what this machine can do.
    ///
    /// <para>
    /// Each probe is guarded on its own. A driver query that fails degrades to "not available" — which is the
    /// honest answer and the safe one — rather than taking down the page a user came to in order to fix
    /// something.
    /// </para>
    /// </summary>
    public async Task LoadAsync()
    {
        // The stored settings first and synchronously, so the page has something real to render immediately
        // rather than flashing defaults while a driver is questioned.
        Mutate(() =>
        {
            _loadError = null;

            try
            {
                _draft = _store.Current;
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                _draft = new RipcordSettings();
            }
        });

        // Then the capabilities, which are native and must not run on the UI thread — see
        // IVideoCapabilitiesProbe, where that is a correctness requirement rather than a preference.
        bool hevc = await ProbeAsync(_capabilities.IsHevcDecodeAvailableAsync).ConfigureAwait(false);
        bool hdr = await ProbeAsync(_capabilities.IsHdrDisplayAvailableAsync).ConfigureAwait(false);

        Mutate(() =>
        {
            _hevcAvailable = hevc;
            _displayHdr = hdr;

            // HDR rides on HEVC, so a machine that cannot decode HEVC cannot honour a stored HDR request.
            // Correcting the draft rather than only the toggle keeps the saved record from claiming something
            // untrue the moment anything else on the page is changed.
            if (!_hevcAvailable && (_draft.Codec == VideoCodec.Hevc || _draft.RequestHdr))
            {
                _draft = _draft with { Codec = VideoCodec.H264, RequestHdr = false };
            }
        });

        if (_draft.GpuPreference == GpuPreference.Specific)
        {
            await EnsureAdaptersAsync().ConfigureAwait(false);
        }
    }

    // ---- setters -------------------------------------------------------------------------------
    //
    // Each takes the value the user chose, not an event. Each is a no-op when the value is what is already held
    // — see the note at the top of the class for why that, rather than a flag, is what makes parse-time and
    // render-time events harmless.

    public void SetResolution(int index) => Apply(() =>
    {
        (int width, int height, int fps) = Resolutions[Math.Clamp(index, 0, Resolutions.Length - 1)];
        return _draft with { Width = width, Height = height, TargetFps = fps };
    });

    public void SetBitrateMbps(double mbps)
        => Apply(() => _draft with { BitrateKbps = (int)Math.Round(mbps * 1000) });

    public void SetUpscale(int index)
        => Apply(() => _draft with { UpscaleMode = index == 1 ? UpscaleMode.FsrFallback : UpscaleMode.None });

    public void SetAdaptiveQuality(bool on) => Apply(() => _draft with { AdaptiveQuality = on });

    public void SetReportConnectionQuality(bool on) => Apply(() => _draft with { ReportConnectionQuality = on });

    /// <summary>
    /// Choose a codec.
    ///
    /// <para>
    /// Switching away from HEVC clears the HDR request rather than leaving it set-but-disabled, which would
    /// silently reappear on a later switch back. Both are changed in one record, so the state cannot be observed
    /// mid-way with HDR still requested over H.264.
    /// </para>
    /// </summary>
    public void SetCodec(int index) => Apply(() =>
    {
        bool hevc = index == 1 && _hevcAvailable;
        return _draft with
        {
            Codec = hevc ? VideoCodec.Hevc : VideoCodec.H264,
            RequestHdr = hevc && _draft.RequestHdr,
        };
    });

    public void SetRequestHdr(bool on) => Apply(() => _draft with { RequestHdr = on && HdrSelectable });

    /// <summary>
    /// Choose how the GPU is picked.
    ///
    /// <para>
    /// Returns a Task because choosing "a specific GPU" is the one setting whose consequences have to be
    /// fetched: the adapter list is a native walk that cannot run on the UI thread. The preference itself
    /// applies immediately, so the picker responds at once and the options fill in behind it.
    /// </para>
    /// </summary>
    public async Task SetGpuPreferenceAsync(int index)
    {
        var preference = (GpuPreference)Math.Clamp(index, 0, GpuOptions.Length - 1);

        // Drop a pinned LUID when the preference stops being "specific": leaving it set means a stale adapter id
        // rides along in the saved record and comes back if the user ever returns to Specific, pointing at a GPU
        // that may no longer be installed.
        Apply(() => _draft with
        {
            GpuPreference = preference,
            GpuLuid = preference == GpuPreference.Specific ? _draft.GpuLuid : 0,
        });

        if (preference == GpuPreference.Specific)
        {
            await EnsureAdaptersAsync().ConfigureAwait(false);
        }
    }

    public void SetAdapter(int index) => Apply(() =>
    {
        IReadOnlyList<VideoAdapterOption> adapters = _adapters ?? [];
        ulong luid = index >= 0 && index < adapters.Count ? adapters[index].Luid : 0;
        return _draft with { GpuLuid = luid };
    });

    public void SetKeyboardEnabled(bool on)
        => Apply(() => _draft with { InputBindings = _draft.InputBindings with { KeyboardEnabled = on } });

    /// <summary>
    /// Accept edited bindings from the rebinding dialog. The keyboard toggle lives in the same record, so the
    /// dialog's result is merged onto the current toggle state rather than replacing the whole thing.
    /// </summary>
    public void SetInputBindings(InputBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        Apply(() => _draft with
        {
            InputBindings = bindings with { KeyboardEnabled = _draft.InputBindings.KeyboardEnabled },
        });
    }

    public void SetExitGesture(int index)
        => Apply(() => _draft with
        {
            ExitGesture = (ExitGesture)Math.Clamp(index, 0, ExitGestureOptions.Length - 1),
        });

    public void SetDeadzone(double value) => Apply(() => _draft with { UiStickDeadzone = value });

    public void SetFullScreenOnConnect(bool on) => Apply(() => _draft with { FullScreenOnConnect = on });

    public void SetConfirmOnDisconnect(bool on) => Apply(() => _draft with { ConfirmOnDisconnect = on });

    public void SetRestOnDisconnect(bool on) => Apply(() => _draft with { RestConsoleOnDisconnect = on });

    /// <summary>
    /// Pick how much of the HUD a stream opens at. Goes through
    /// <see cref="RipcordSettings.WithDiagnosticsRung"/> so the legacy bool stays in step — a user who moves
    /// between builds should not lose the preference in either direction.
    /// </summary>
    public void SetDiagnosticsRung(int index) => Apply(
        () => _draft.WithDiagnosticsRung(DiagnosticsRungs[Math.Clamp(index, 0, DiagnosticsRungs.Length - 1)]));

    // ---- internals -----------------------------------------------------------------------------

    /// <summary>The rungs offered, in the order they are shown. Index into this, never cast.</summary>
    private static readonly DiagnosticsRung[] DiagnosticsRungs =
        [DiagnosticsRung.Hidden, DiagnosticsRung.Summary, DiagnosticsRung.Full];

    private static readonly string[] DiagnosticsLabels =
    [
        Strings.Settings_DiagnosticsOff,
        Strings.Settings_DiagnosticsSummary,
        Strings.Settings_DiagnosticsFull,
    ];

    /// <summary>
    /// Adopt a new draft and persist it — but only if it differs. This is the one place a save happens, and the
    /// equality check is the whole reason no caller needs a suppression flag.
    /// </summary>
    private void Apply(Func<RipcordSettings> next) => Mutate(() =>
    {
        RipcordSettings candidate = next();
        if (candidate == _draft)
        {
            return;
        }

        _draft = candidate;

        try
        {
            _store.Save(candidate);
        }
        catch (Exception ex)
        {
            // A settings file that cannot be written is worth saying so about — silently discarding the user's
            // choice while the control shows it applied is the worst of both.
            _loadError = string.Format(Strings.Settings_SaveFailed, ex.Message);
        }
    });

    private async Task EnsureAdaptersAsync()
    {
        if (_adapters is not null)
        {
            return;
        }

        IReadOnlyList<VideoAdapterOption> adapters;
        string? error;

        try
        {
            adapters = await _capabilities.EnumerateAdaptersAsync().ConfigureAwait(false);
            error = null;
        }
        catch (Exception ex)
        {
            adapters = [];
            error = string.Format(Strings.Settings_AdapterListFailed, ex.Message);
        }

        Mutate(() =>
        {
            _adapters = adapters;
            _adapterError = error;
        });
    }

    private static async Task<bool> ProbeAsync(Func<Task<bool>> query)
    {
        try
        {
            return await query().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Degrade to "not available", which is both the honest answer when we cannot tell and the one that
            // cannot promise something the stream will fail to deliver.
            return false;
        }
    }

    /// <summary>HDR is only offerable with HEVC selected AND available: 10-bit is the prerequisite.</summary>
    private bool HdrSelectable => _hevcAvailable && _draft.Codec == VideoCodec.Hevc;

    protected override SettingsViewState Compose()
    {
        int resolutionIndex = Array.FindIndex(
            Resolutions,
            o => o.Width == _draft.Width && o.Height == _draft.Height && o.Fps == _draft.TargetFps);

        bool hevcSelected = _draft.Codec == VideoCodec.Hevc && _hevcAvailable;
        IReadOnlyList<VideoAdapterOption> adapters = _adapters ?? [];
        bool specific = _draft.GpuPreference == GpuPreference.Specific;

        // Warn only once the list has actually been read, and only when nothing on it would work — an empty list
        // means "not enumerated yet", which is not a finding.
        bool noneUsable = adapters.Count > 0 && adapters.All(a => !a.SupportsHardwareDecode);
        string adapterWarning = _adapterError ?? (noneUsable
            ? Strings.Settings_NoHardwareDecode
            : string.Empty);

        return new SettingsViewState(
            ResolutionOptions: ResolutionLabels,
            ResolutionIndex: resolutionIndex >= 0 ? resolutionIndex : DefaultResolutionIndex,
            BitrateMbps: _draft.BitrateKbps / 1000.0,
            BitrateLabel: string.Format(Strings.Settings_BitrateMbps, _draft.BitrateKbps / 1000.0),
            UpscaleIndex: _draft.UpscaleMode == UpscaleMode.FsrFallback ? 1 : 0,
            AdaptiveQuality: _draft.AdaptiveQuality,
            ReportConnectionQuality: _draft.ReportConnectionQuality,

            // HEVC is offered only where a decoder exists: the codec is requested in the launchSpec at connect,
            // so offering it on a machine that cannot decode it produces a stream that arrives and never
            // displays.
            CodecOptions: _hevcAvailable ? [H264Label, HevcLabel] : [H264Label],
            CodecIndex: hevcSelected ? 1 : 0,
            CodecPickerEnabled: _hevcAvailable,
            CodecHelp: ComposeCodecHelp(),
            RequestHdr: _draft.RequestHdr && HdrSelectable,
            HdrToggleEnabled: HdrSelectable,
            HdrChecks: ComposeHdrChecks(hevcSelected),
            HdrHelp: _hevcAvailable && hevcSelected && _displayHdr
                // Say so when the machine is ready. Three ticks plus a sentence about what happens if something
                // is missing leaves the reader to work out that nothing is; an affirmative line is shorter and
                // is the answer they came for.
                ? Strings.Settings_HdrReady
                : Strings.Settings_HdrToneMapped,

            GpuPreferenceIndex: (int)_draft.GpuPreference,
            AdapterPickerVisible: specific,
            AdapterOptions: adapters.Select(DescribeAdapter).ToArray(),
            AdapterIndex: IndexOfPinnedAdapter(adapters),
            AdapterWarning: adapterWarning,
            AdapterWarningVisible: specific && adapterWarning.Length > 0,

            KeyboardEnabled: _draft.InputBindings.KeyboardEnabled,
            KeyboardSummary: ComposeKeyboardSummary(),
            ExitGestureIndex: (int)_draft.ExitGesture,
            ExitGestureDescription: ComposeExitGestureDescription(),
            UiStickDeadzone: _draft.UiStickDeadzone,
            DeadzoneLabel: $"{_draft.UiStickDeadzone:F2}",

            FullScreenOnConnect: _draft.FullScreenOnConnect,
            ConfirmOnDisconnect: _draft.ConfirmOnDisconnect,
            RestConsoleOnDisconnect: _draft.RestConsoleOnDisconnect,
            DiagnosticsOptions: DiagnosticsLabels,
            DiagnosticsIndex: Array.IndexOf(DiagnosticsRungs, _draft.DiagnosticsRungOnConnect),

            CredentialTitle: _consoles.CredentialsEncrypted
                ? Strings.Settings_ConsolesEncrypted
                : Strings.Settings_ConsolesNotEncrypted,
            CredentialMessage: _consoles.CredentialsEncrypted
                ? string.Format(Strings.Settings_CredentialsProtected, _consoles.ProtectionDescription)
                // Never imply protection that is not there.
                : string.Format(Strings.Settings_CredentialsUnprotected, _consoles.ProtectionDescription),
            CredentialTone: _consoles.CredentialsEncrypted ? StatusTone.Positive : StatusTone.Caution,

            LoadError: _loadError);
    }

    /// <summary>
    /// <b>What HEVC actually buys, measured rather than assumed:</b> nothing in resolution terms. The console
    /// picks its resolution rung from the declared bandwidth and gives the codec no credit for efficiency —
    /// tested at a 5 Mbps cap, both H.264 and HEVC settled on 960×540. At a 40 Mbps cap HEVC spent MORE
    /// bandwidth than H.264 (35.9 vs 22.2 Mbps): it banks its efficiency as picture quality at the same
    /// resolution rather than as savings. So this is a quality option, not a way to get a higher resolution on a
    /// limited connection, and the help text must not promise the latter.
    /// </summary>
    private string ComposeCodecHelp() => _hevcAvailable
        ? Strings.Settings_HevcExplained
        : Strings.Settings_HevcUnavailable;

    /// <summary>
    /// Four prerequisites for HDR, of which this app controls one — so a user whose picture stays SDR needs to
    /// see WHICH is missing rather than a sentence listing all of them.
    /// </summary>
    private IReadOnlyList<HdrCheck> ComposeHdrChecks(bool hevcSelected) =>
    [
        new HdrCheck(
            _hevcAvailable && hevcSelected ? HdrCheckState.Met : HdrCheckState.Unmet,
            Strings.Settings_HdrCheckHevcCodec,
            !_hevcAvailable
                ? Strings.Settings_HdrNoHevcDecoder
                : hevcSelected ? null : Strings.Settings_HdrChooseHevc),

        new HdrCheck(
            _displayHdr ? HdrCheckState.Met : HdrCheckState.Unmet,
            Strings.Settings_HdrCheckDisplayMode,
            _displayHdr ? null : Strings.Settings_HdrTurnOnInWindows),

        new HdrCheck(
            HdrCheckState.Unknown,
            Strings.Settings_HdrCheckConsoleSends,
            Strings.Settings_HdrCheckedOnConnect),
    ];

    private string ComposeKeyboardSummary()
    {
        int bound = _draft.InputBindings.Keyboard.Count;
        int remapped = _draft.InputBindings.GamepadRemap.Count;

        return remapped > 0
            ? string.Format(Strings.Settings_KeysBoundAndRemapped, bound, remapped)
            : string.Format(Strings.Settings_KeysBound, bound);
    }

    private string ComposeExitGestureDescription() => _draft.ExitGesture == ExitGesture.None
        ? Strings.Settings_ExitNoGesture
        : string.Format(Strings.Settings_ExitGestureHeld, ExitGestureDetector.Describe(_draft.ExitGesture));

    /// <summary>Say plainly why an adapter is a poor choice rather than letting someone pick one that fails.</summary>
    private static string DescribeAdapter(VideoAdapterOption adapter)
    {
        string note = !adapter.SupportsHardwareDecode
            ? Strings.Settings_AdapterNoHardwareDecode
            : adapter.DrivesADisplay ? string.Empty : Strings.Settings_AdapterNotDrivingDisplay;

        return $"{adapter.Description}{note}";
    }

    private int IndexOfPinnedAdapter(IReadOnlyList<VideoAdapterOption> adapters)
    {
        for (int i = 0; i < adapters.Count; i++)
        {
            if (adapters[i].Luid == _draft.GpuLuid)
            {
                return i;
            }
        }

        return adapters.Count > 0 ? 0 : -1;
    }
}
