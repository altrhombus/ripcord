using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Diagnostics;
using Ripcord.Core.Input;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Media.Interop;
using Ripcord_App.Dialogs;
using Ripcord_App.Services;

namespace Ripcord_App.Pages;

/// <summary>
/// The tunables. Everything here was previously a compile-time constant scattered across the app, which is why
/// this page used to read "This is the Settings page".
///
/// <para>
/// Changes save immediately — there is no OK/Cancel, matching how Windows 11 settings behave. Video and GPU
/// changes apply to the <em>next</em> connection, because both require rebuilding the decode pipeline (and, for
/// the GPU, a new device and swap chain).
/// </para>
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly ISettingsStore _store = new SettingsStore();

    /// <summary>
    /// Suppresses saves that are not user edits. Starts <c>true</c>, and that matters: XAML parsing itself
    /// raises change events. Setting <c>Slider.Minimum="2"</c> during InitializeComponent moves Value from 0
    /// to 2 and fires ValueChanged — before the controls declared later in the file exist — so a save
    /// triggered then both misrepresents the user's intent and dereferences nulls. Cleared once the initial
    /// population finishes.
    /// </summary>
    private bool _loading = true;

    private IReadOnlyList<VideoAdapterInfo> _adapters = [];

    private static readonly (string Label, int Width, int Height, int Fps)[] ResolutionOptions =
    [
        ("1080p 60 fps", 1920, 1080, 60),
        ("1080p 30 fps", 1920, 1080, 30),
        ("720p 60 fps", 1280, 720, 60),
        ("720p 30 fps", 1280, 720, 30),
        ("540p 60 fps", 960, 540, 60),
    ];

    /// <summary>Index of the 720p60 entry, used as the fallback selection.</summary>
    private const int DefaultResolutionIndex = 2;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            RipcordSettings s = _store.Current;

            PopulateResolutions(s);
            PopulateUpscale(s);
            PopulateCodec(s);
            PopulateKeyboard(s);
            PopulateGpu(s);
            PopulateExitGesture(s);

            BitrateSlider.Value = Math.Clamp(s.BitrateKbps / 1000.0, BitrateSlider.Minimum, BitrateSlider.Maximum);
            UpdateBitrateText();

            DeadzoneSlider.Value = Math.Clamp(s.UiStickDeadzone, DeadzoneSlider.Minimum, DeadzoneSlider.Maximum);
            UpdateDeadzoneText();

            AdaptiveToggle.IsOn = s.AdaptiveQuality;
            ConnectionQualityToggle.IsOn = s.ReportConnectionQuality;
            FullScreenToggle.IsOn = s.FullScreenOnConnect;
            DiagnosticsToggle.IsOn = s.ShowDiagnosticsOverlay;
            LargeUiToggle.IsOn = s.LargeUiScale;

            ShowCredentialProtectionStatus();
        }
        catch (Exception ex)
        {
            // This page reads native adapter data and the credential store. A failure in either should degrade
            // to a visible message on this page, never take down the app — the settings page is also where a
            // user goes to fix a bad configuration, so it must stay reachable.
            CredentialProtectionBar.Severity = InfoBarSeverity.Error;
            CredentialProtectionBar.Title = "Some settings couldn't be loaded";
            CredentialProtectionBar.Message = ex.Message;
            CredentialProtectionBar.IsOpen = true;
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- population ----

    private void PopulateResolutions(RipcordSettings s)
    {
        ResolutionCombo.Items.Clear();
        foreach ((string label, _, _, _) in ResolutionOptions)
        {
            ResolutionCombo.Items.Add(label);
        }

        int index = Array.FindIndex(
            ResolutionOptions, o => o.Width == s.Width && o.Height == s.Height && o.Fps == s.TargetFps);
        ResolutionCombo.SelectedIndex = index >= 0 ? index : DefaultResolutionIndex;
    }

    private void PopulateUpscale(RipcordSettings s)
    {
        UpscaleCombo.Items.Clear();
        UpscaleCombo.Items.Add("Smooth (bilinear)");
        UpscaleCombo.Items.Add("Sharp (bicubic)");
        UpscaleCombo.SelectedIndex = s.UpscaleMode == UpscaleMode.FsrFallback ? 1 : 0;
    }

    /// <summary>
    /// Fill the codec picker, offering HEVC only if a decoder for it exists here.
    ///
    /// <para>
    /// HEVC decode depends on the GPU and on the HEVC Video Extension being present, and the codec is requested
    /// in the launchSpec at connect: offering it on a machine that cannot decode it would produce a stream that
    /// arrives and never displays. Hence the capability query rather than a bare option.
    /// </para>
    ///
    /// <para>
    /// <b>What HEVC actually buys, measured rather than assumed:</b> nothing in resolution terms. The console
    /// picks its resolution rung from the declared bandwidth and gives the codec no credit for efficiency —
    /// tested at a 5 Mbps cap, both H.264 and HEVC settled on 960x540. At a 40 Mbps cap HEVC spent MORE
    /// bandwidth than H.264 (35.9 vs 22.2 Mbps), i.e. it banks its efficiency as picture quality at the same
    /// resolution rather than as savings. So this is a quality option, not a way to get a higher resolution on a
    /// limited connection, and the help text must not promise the latter.
    /// </para>
    ///
    /// <para>
    /// It gained a second, concrete reason on 2026-08-02: <b>HEVC is the prerequisite for HDR</b>. The console
    /// answers a <c>dynamicRange: "HDR"</c> request with HEVC Main10 signalling PQ and BT.2020 — verified on
    /// hardware — and the AVC it offers is 8-bit, so the HDR toggle is gated on this picker. That is a real
    /// difference a user can see, unlike the efficiency argument above, which the console declines to pass on.
    /// </para>
    /// </summary>
    private void PopulateCodec(RipcordSettings s)
    {
        bool hevcAvailable;
        try
        {
            hevcAvailable = VideoCapabilities.IsCodecDecodeAvailable(VideoCodecKind.Hevc);
        }
        catch (Exception ex)
        {
            // Native query failed: degrade to "H.264 only" rather than taking the page down.
            Debug.WriteLine($"[Ripcord] HEVC capability query failed: {ex.Message}");
            hevcAvailable = false;
        }

        CodecCombo.Items.Clear();
        CodecCombo.Items.Add("H.264 (compatible)");
        if (hevcAvailable)
        {
            CodecCombo.Items.Add("HEVC (better quality per Mbps)");
        }

        bool wantHevc = s.Codec == VideoCodec.Hevc && hevcAvailable;
        CodecCombo.SelectedIndex = wantHevc ? 1 : 0;
        CodecCombo.IsEnabled = hevcAvailable;

        // HDR rides on HEVC: 10-bit is the prerequisite and only the HEVC path can carry it.
        bool hdrSelectable = hevcAvailable && CodecCombo.SelectedIndex == 1;
        HdrToggle.IsOn = s.RequestHdr && hdrSelectable;
        HdrToggle.IsEnabled = hdrSelectable;
        HdrHelpText.Text = HdrHelpFor(hdrSelectable);

        CodecHelpText.Text = hevcAvailable
            ? "HEVC uses the available bandwidth more efficiently, so the picture can look cleaner at the same "
              + "resolution. It does not change which resolution the console sends. Takes effect on the next "
              + "connection."
            : "HEVC is unavailable on this PC — no HEVC decoder is installed. Takes effect on the next connection.";
    }

    private void PopulateKeyboard(RipcordSettings s)
    {
        KeyboardToggle.IsOn = s.InputBindings.KeyboardEnabled;
        UpdateKeyboardSummary(s.InputBindings);
    }

    private void UpdateKeyboardSummary(InputBindings bindings)
    {
        int bound = bindings.Keyboard.Count;
        int remapped = bindings.GamepadRemap.Count;
        KeyboardSummaryText.Text = remapped > 0
            ? $"{bound} keys bound · {remapped} gamepad buttons remapped"
            : $"{bound} keys bound";
    }

    /// <summary>
    /// Open the rebinding dialog. The toggle and the bindings are stored together, so the dialog's result is
    /// merged with the current toggle state rather than replacing the whole record.
    /// </summary>
    private async void KeyBindings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new KeyBindingsDialog(_store.Current.InputBindings) { XamlRoot = XamlRoot };
            await dialog.ShowAsync();

            InputBindings edited = dialog.Result with { KeyboardEnabled = KeyboardToggle.IsOn };
            _store.Save(_store.Current with { InputBindings = edited });
            UpdateKeyboardSummary(edited);
        }
        catch (Exception ex)
        {
            // async void: this cannot be allowed to throw into the message loop.
            Debug.WriteLine($"[Ripcord] key bindings dialog failed: {ex.Message}");
        }
    }

    private void PopulateGpu(RipcordSettings s)
    {
        GpuCombo.Items.Clear();
        GpuCombo.Items.Add("Automatic (recommended)");
        GpuCombo.Items.Add("Prefer battery life");
        GpuCombo.Items.Add("Prefer performance");
        GpuCombo.Items.Add("Choose a specific GPU");
        GpuCombo.SelectedIndex = (int)s.GpuPreference;

        // The adapter list is populated lazily — see EnsureAdaptersPopulated. Enumerating adapters creates and
        // destroys a D3D12 device per adapter to test decode capability, which is far too much work to do on
        // every visit to this page when almost nobody picks a specific GPU.
        UpdateAdapterVisibility(s.GpuPreference);
    }

    /// <summary>
    /// Fill the adapter picker on first need. Isolated and independently guarded because it is the only place
    /// this page calls into native code, so a failure here degrades to "you can't pin a GPU" rather than
    /// taking the page — or the app — with it.
    /// </summary>
    private void EnsureAdaptersPopulated()
    {
        if (_adapters.Count > 0)
        {
            return;
        }

        try
        {
            _adapters = VideoCapabilities.EnumerateAdapters().ToList();
        }
        catch (Exception ex)
        {
            _adapters = [];
            AdapterWarning.Severity = InfoBarSeverity.Error;
            AdapterWarning.Message = $"Couldn't list the graphics adapters on this PC: {ex.Message}";
            AdapterWarning.IsOpen = true;
            return;
        }

        AdapterCombo.Items.Clear();
        foreach (VideoAdapterInfo a in _adapters)
        {
            // Say plainly why an adapter is a poor choice rather than letting the user pick one that fails.
            string note = !a.SupportsHardwareDecode
                ? " — no hardware video decoding"
                : a.DrivesADisplay ? string.Empty : " — not driving a display (adds a copy each frame)";
            AdapterCombo.Items.Add($"{a.Description}{note}");
        }

        ulong savedLuid = _store.Current.GpuLuid;
        int selected = -1;
        for (int i = 0; i < _adapters.Count; i++)
        {
            if (_adapters[i].Luid == savedLuid)
            {
                selected = i;
                break;
            }
        }

        AdapterCombo.SelectedIndex = selected >= 0 ? selected : (_adapters.Count > 0 ? 0 : -1);
    }

    private void PopulateExitGesture(RipcordSettings s)
    {
        ExitGestureCombo.Items.Clear();
        ExitGestureCombo.Items.Add("Options + Create + L1 + R1");
        ExitGestureCombo.Items.Add("Both sticks pressed (L3 + R3)");
        ExitGestureCombo.Items.Add("Keyboard only (Esc)");
        ExitGestureCombo.SelectedIndex = (int)s.ExitGesture;
        UpdateExitGestureDescription();
    }

    private void ShowCredentialProtectionStatus()
    {
        var store = new PairedConsoleStore();
        if (store.CredentialsEncrypted)
        {
            CredentialProtectionBar.Severity = InfoBarSeverity.Success;
            CredentialProtectionBar.Title = "Saved consoles are encrypted";
            CredentialProtectionBar.Message =
                $"Pairing credentials are protected with {store.ProtectionDescription} and can only be read by "
                + "your Windows account on this PC.";
        }
        else
        {
            // Never imply protection that is not there.
            CredentialProtectionBar.Severity = InfoBarSeverity.Warning;
            CredentialProtectionBar.Title = "Saved consoles are not encrypted";
            CredentialProtectionBar.Message =
                $"Pairing credentials are stored {store.ProtectionDescription}. Anyone who can read your user "
                + "folder could copy them.";
        }
    }

    // ---- change handlers ----

    // Every handler returns early while _loading. Belt and braces alongside the guard inside Save(): these run
    // during XAML parse as well as on real interaction, and at parse time the controls they touch may not exist
    // yet — so the check has to come before any control access, not just before persisting.

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            Save();
        }
    }

    private void OnSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            Save();
        }
    }

    private void OnBitrateChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateBitrateText();
        Save();
    }

    private void OnDeadzoneChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateDeadzoneText();
        Save();
    }

    /// <summary>
    /// Codec changed: HDR is only offered with HEVC, so its toggle has to follow within this visit rather than
    /// waiting for the page to be reopened. Switching to H.264 clears the HDR request instead of leaving it
    /// set-but-disabled, which would silently reappear on a later switch back to HEVC.
    /// </summary>
    private void OnCodecChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool hdrSelectable = CodecCombo.SelectedIndex == 1;
        if (!hdrSelectable)
        {
            HdrToggle.IsOn = false;
        }

        HdrToggle.IsEnabled = hdrSelectable;
        HdrHelpText.Text = HdrHelpFor(hdrSelectable);

        Save();
    }

    /// <summary>Shared so the initial population and the codec-change path cannot drift apart.</summary>
    private static string HdrHelpFor(bool selectable) => selectable
        ? "The console streams HDR (PQ, BT.2020) when asked. Three things outside this app also have to be true: "
          + "HDR enabled on the PS5, an HDR display, and Use HDR turned on in Windows display settings. "
          + "Without all three the picture is tone-mapped to SDR, which still looks correct, just flatter. "
          + "The diagnostics overlay (F3) reports which one you are getting."
        : "HDR requires HEVC — select it above first.";

    private void OnGpuPreferenceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateAdapterVisibility((GpuPreference)Math.Clamp(GpuCombo.SelectedIndex, 0, 3));
        Save();
    }

    private void OnExitGestureChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateExitGestureDescription();
        Save();
    }

    private void UpdateAdapterVisibility(GpuPreference preference)
    {
        bool specific = preference == GpuPreference.Specific;
        AdapterCombo.Visibility = specific ? Visibility.Visible : Visibility.Collapsed;

        if (!specific)
        {
            AdapterWarning.IsOpen = false;
            return;
        }

        // Only now is the adapter list actually needed.
        EnsureAdaptersPopulated();

        bool noneUsable = _adapters.Count > 0 && _adapters.All(a => !a.SupportsHardwareDecode);
        if (noneUsable)
        {
            AdapterWarning.Severity = InfoBarSeverity.Warning;
            AdapterWarning.Message =
                "None of the installed GPUs report hardware video decoding. Streaming will fall back to the CPU, "
                + "which uses far more power.";
            AdapterWarning.IsOpen = true;
        }
    }

    private void UpdateExitGestureDescription()
    {
        var gesture = (ExitGesture)Math.Clamp(ExitGestureCombo.SelectedIndex, 0, 2);
        ExitGestureDescription.Text = gesture == ExitGesture.None
            ? "No controller gesture. You will need a keyboard (Esc) or the on-screen button to leave a stream."
            : $"{ExitGestureDetector.Describe(gesture)}, held briefly. The PS button is deliberately not used — "
              + "it has to reach the console.";
    }

    private void UpdateBitrateText() => BitrateValueText.Text = $"{BitrateSlider.Value:F0} Mbps";

    private void UpdateDeadzoneText() => DeadzoneValueText.Text = $"{DeadzoneSlider.Value:F2}";

    // ---- persistence ----

    private void Save()
    {
        if (_loading)
        {
            return;
        }

        (_, int width, int height, int fps) = ResolutionOptions[
            Math.Clamp(ResolutionCombo.SelectedIndex, 0, ResolutionOptions.Length - 1)];

        var preference = (GpuPreference)Math.Clamp(GpuCombo.SelectedIndex, 0, 3);
        ulong luid = preference == GpuPreference.Specific
                     && AdapterCombo.SelectedIndex >= 0
                     && AdapterCombo.SelectedIndex < _adapters.Count
            ? _adapters[AdapterCombo.SelectedIndex].Luid
            : 0;

        _store.Save(_store.Current with
        {
            Width = width,
            Height = height,
            TargetFps = fps,
            BitrateKbps = (int)(BitrateSlider.Value * 1000),
            UpscaleMode = UpscaleCombo.SelectedIndex == 1 ? UpscaleMode.FsrFallback : UpscaleMode.None,
            AdaptiveQuality = AdaptiveToggle.IsOn,
            ReportConnectionQuality = ConnectionQualityToggle.IsOn,
            Codec = CodecCombo.SelectedIndex == 1 ? VideoCodec.Hevc : VideoCodec.H264,
            RequestHdr = HdrToggle.IsOn && CodecCombo.SelectedIndex == 1,
            InputBindings = _store.Current.InputBindings with { KeyboardEnabled = KeyboardToggle.IsOn },
            GpuPreference = preference,
            GpuLuid = luid,
            ExitGesture = (ExitGesture)Math.Clamp(ExitGestureCombo.SelectedIndex, 0, 2),
            UiStickDeadzone = DeadzoneSlider.Value,
            FullScreenOnConnect = FullScreenToggle.IsOn,
            ShowDiagnosticsOverlay = DiagnosticsToggle.IsOn,
            LargeUiScale = LargeUiToggle.IsOn,
        });
    }
}
