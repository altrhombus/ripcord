using Ripcord.Core.Consoles;
using Ripcord.Core.Input;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Threading;

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
    private static readonly (string Label, int Width, int Height, int Fps)[] Resolutions =
    [
        ("1080p 60 fps", 1920, 1080, 60),
        ("1080p 30 fps", 1920, 1080, 30),
        ("720p 60 fps", 1280, 720, 60),
        ("720p 30 fps", 1280, 720, 30),
        ("540p 60 fps", 960, 540, 60),
    ];

    /// <summary>Index of the 720p60 entry, used when the stored geometry matches no offered row.</summary>
    private const int DefaultResolutionIndex = 2;

    private const string H264Label = "H.264 (compatible)";
    private const string HevcLabel = "HEVC (better quality per Mbps)";

    private static readonly string[] UpscaleOptions = ["Smooth (bilinear)", "Sharp (bicubic)"];

    private static readonly string[] GpuOptions =
    [
        "Automatic (recommended)",
        "Prefer battery life",
        "Prefer performance",
        "Choose a specific GPU",
    ];

    private static readonly string[] ExitGestureOptions =
    [
        "Options + Create + L1 + R1",
        "Both sticks pressed (L3 + R3)",
        "Keyboard only (Esc)",
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
    public void Load() => Mutate(() =>
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

        _hevcAvailable = Probe(_capabilities.IsHevcDecodeAvailable);
        _displayHdr = Probe(_capabilities.IsHdrDisplayAvailable);

        // HDR rides on HEVC, so a machine that cannot decode HEVC cannot honour a stored HDR request. Correcting
        // the draft rather than only the toggle keeps the saved record from claiming something untrue the moment
        // anything else on the page is changed.
        if (!_hevcAvailable && (_draft.Codec == VideoCodec.Hevc || _draft.RequestHdr))
        {
            _draft = _draft with { Codec = VideoCodec.H264, RequestHdr = false };
        }

        if (_draft.GpuPreference == GpuPreference.Specific)
        {
            EnsureAdapters();
        }
    });

    // ---- setters -------------------------------------------------------------------------------
    //
    // Each takes the value the user chose, not an event. Each is a no-op when the value is what is already held
    // — see the note at the top of the class for why that, rather than a flag, is what makes parse-time and
    // render-time events harmless.

    public void SetResolution(int index) => Apply(() =>
    {
        (_, int width, int height, int fps) = Resolutions[Math.Clamp(index, 0, Resolutions.Length - 1)];
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

    public void SetGpuPreference(int index) => Apply(() =>
    {
        var preference = (GpuPreference)Math.Clamp(index, 0, GpuOptions.Length - 1);

        if (preference == GpuPreference.Specific)
        {
            EnsureAdapters();
        }

        // Drop a pinned LUID when the preference stops being "specific": leaving it set means a stale adapter id
        // rides along in the saved record and comes back if the user ever returns to Specific, pointing at a GPU
        // that may no longer be installed.
        return _draft with
        {
            GpuPreference = preference,
            GpuLuid = preference == GpuPreference.Specific ? _draft.GpuLuid : 0,
        };
    });

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

    public void SetShowDiagnostics(bool on) => Apply(() => _draft with { ShowDiagnosticsOverlay = on });

    public void SetLargeUiScale(bool on) => Apply(() => _draft with { LargeUiScale = on });

    // ---- internals -----------------------------------------------------------------------------

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
            _loadError = $"Couldn't save settings: {ex.Message}";
        }
    });

    private void EnsureAdapters()
    {
        if (_adapters is not null)
        {
            return;
        }

        try
        {
            _adapters = _capabilities.EnumerateAdapters();
            _adapterError = null;
        }
        catch (Exception ex)
        {
            _adapters = [];
            _adapterError = $"Couldn't list the graphics adapters on this PC: {ex.Message}";
        }
    }

    private static bool Probe(Func<bool> query)
    {
        try
        {
            return query();
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
            ? "None of the installed GPUs report hardware video decoding. Streaming will fall back to the CPU, "
              + "which uses far more power."
            : string.Empty);

        return new SettingsViewState(
            ResolutionOptions: Resolutions.Select(r => r.Label).ToArray(),
            ResolutionIndex: resolutionIndex >= 0 ? resolutionIndex : DefaultResolutionIndex,
            BitrateMbps: _draft.BitrateKbps / 1000.0,
            BitrateLabel: $"{_draft.BitrateKbps / 1000.0:F0} Mbps",
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
                ? "This PC is ready for HDR. Whether a given game streams in HDR is up to the console."
                : "Tone-mapped to SDR if any of the above is missing, which still looks correct, just flatter.",

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
            ShowDiagnosticsOverlay: _draft.ShowDiagnosticsOverlay,
            LargeUiScale: _draft.LargeUiScale,

            CredentialTitle: _consoles.CredentialsEncrypted
                ? "Saved consoles are encrypted"
                : "Saved consoles are not encrypted",
            CredentialMessage: _consoles.CredentialsEncrypted
                ? $"Pairing credentials are protected with {_consoles.ProtectionDescription} and can only be "
                  + "read by your Windows account on this PC."
                // Never imply protection that is not there.
                : $"Pairing credentials are stored {_consoles.ProtectionDescription}. Anyone who can read your "
                  + "user folder could copy them.",
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
        ? "HEVC uses the available bandwidth more efficiently, so the picture can look cleaner at the same "
          + "resolution. It does not change which resolution the console sends. Takes effect on the next "
          + "connection."
        : "HEVC is unavailable on this PC — no HEVC decoder is installed. Takes effect on the next connection.";

    /// <summary>
    /// Four prerequisites for HDR, of which this app controls one — so a user whose picture stays SDR needs to
    /// see WHICH is missing rather than a sentence listing all of them.
    /// </summary>
    private IReadOnlyList<HdrCheck> ComposeHdrChecks(bool hevcSelected) =>
    [
        new HdrCheck(
            _hevcAvailable && hevcSelected ? HdrCheckState.Met : HdrCheckState.Unmet,
            "HEVC codec",
            !_hevcAvailable
                ? "No HEVC decoder on this PC."
                : hevcSelected ? null : "Choose HEVC in the codec picker above."),

        new HdrCheck(
            _displayHdr ? HdrCheckState.Met : HdrCheckState.Unmet,
            "Display in HDR mode",
            _displayHdr ? null : "Turn on Use HDR in Windows display settings."),

        new HdrCheck(
            HdrCheckState.Unknown,
            "Console sends HDR",
            "Checked once you connect — the diagnostics overlay (F3) reports what arrived."),
    ];

    private string ComposeKeyboardSummary()
    {
        int bound = _draft.InputBindings.Keyboard.Count;
        int remapped = _draft.InputBindings.GamepadRemap.Count;

        return remapped > 0
            ? $"{bound} keys bound · {remapped} gamepad buttons remapped"
            : $"{bound} keys bound";
    }

    private string ComposeExitGestureDescription() => _draft.ExitGesture == ExitGesture.None
        ? "No controller gesture. You will need a keyboard (Esc) or the on-screen button to leave a stream."
        : $"{ExitGestureDetector.Describe(_draft.ExitGesture)}, held briefly. The PS button is deliberately not "
          + "used — it has to reach the console.";

    /// <summary>Say plainly why an adapter is a poor choice rather than letting someone pick one that fails.</summary>
    private static string DescribeAdapter(VideoAdapterOption adapter)
    {
        string note = !adapter.SupportsHardwareDecode
            ? " — no hardware video decoding"
            : adapter.DrivesADisplay ? string.Empty : " — not driving a display (adds a copy each frame)";

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
