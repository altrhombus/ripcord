using System.Text.Json.Serialization;

using Ripcord.Core.Input;
using Ripcord.Core.Sessions;

namespace Ripcord.Core.Settings;

/// <summary>Which GPU to run decode/present on. Mirrors the native GpuSelection so Core stays host-neutral.</summary>
public enum GpuPreference
{
    /// <summary>A decode-capable adapter that drives a display, lowest-power first. Usually the right answer:
    /// on a hybrid laptop the panel hangs off the integrated GPU, so anything else adds a per-frame
    /// cross-adapter copy for a workload that is one decode plus one fullscreen triangle.</summary>
    Auto = 0,
    PreferEfficiency = 1,
    PreferPerformance = 2,
    Specific = 3,
}

/// <summary>How the player leaves a running stream with a controller.</summary>
public enum ExitGesture
{
    /// <summary>Start + Select + L1 + R1 together. Effectively impossible to hit by accident, and it does not
    /// collide with any button the console needs — notably not the PS/Guide button, which must keep reaching
    /// the console to open its own menu.</summary>
    StartSelectShoulders = 0,

    /// <summary>Both sticks clicked together. Quicker, but some games bind L3+R3.</summary>
    BothSticksClicked = 1,

    /// <summary>No controller gesture; keyboard/pointer only.</summary>
    None = 2,
}

/// <summary>
/// Everything the user can tune. One immutable record so it can be handed around, compared, and persisted
/// atomically — and so "what is the current configuration" has exactly one answer.
///
/// <para>
/// Every value here was previously a hardcoded constant somewhere in the app (resolution and bitrate in
/// SessionPage, upscale mode in a floating ComboBox, deadzone in MainWindow, HUD visibility in XAML), which is
/// why none of it was adjustable and the Settings page said "This is the Settings page".
/// </para>
/// </summary>
public sealed record RipcordSettings
{
    // Properties are `set`, not `init`, and that is load-bearing rather than sloppy. This record is read with
    // the System.Text.Json SOURCE GENERATOR (see SettingsStore), and for a record whose properties are all
    // `init` the generator constructs the object with an object-initialiser that assigns EVERY property in the
    // contract - so a property absent from the JSON is written as default(T) rather than left at the value its
    // initialiser gave it. Measured, not assumed: `{ "Width": 1920 }` yields TargetFps 0 with `init` and 60
    // with `set`, while reflection yields 60 either way.
    //
    // The effect is that a settings file written by an older build silently resets every setting it does not
    // mention. `SettingsStoreTests.PartiallyUnknownFile_KeepsWhatItCanAndDefaultsTheRest` pins this, so
    // changing these back to `init` fails the suite rather than quietly wiping preferences on upgrade.
    //
    // A positional record with defaulted parameters also works, if this ever wants its immutability back.

    // ---- video ----

    /// <summary>Requested stream width. 1080p60 at 20 Mbps is the default (owner's decision, 2026-10-05).</summary>
    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public int TargetFps { get; set; } = 60;

    /// <summary>
    /// The starting bitrate asked for. The console aims for about 97% of it and lowers its own rate if the network
    /// can't keep up (research log, 2026-10-02).
    /// </summary>
    public int BitrateKbps { get; set; } = 20_000;

    /// <summary>
    /// H.264 by default, the codec every PC can decode (owner's decision, 2026-10-05). HEVC, and HDR with it, is a
    /// choice in Settings. A stored HEVC on a PC that can't decode it falls back to H.264 at connect (ConnectFlow).
    /// </summary>
    public VideoCodec Codec { get; set; } = VideoCodec.H264;

    /// <summary>
    /// Request HDR. Only meaningful with <see cref="VideoCodec.Hevc"/>, since HDR needs a 10-bit stream and the
    /// AVC path the console offers is 8-bit.
    ///
    /// <para>
    /// Off by default (owner's decision, 2026-10-05), with H.264. What turning it on buys: with the console's HDR on,
    /// under either "Always On" or "On When Supported", the SDR stream it sends for an HDR game has its highlights
    /// clipped by the console; the HDR stream does not, and Ripcord shows it natively on an HDR display and tone-maps
    /// it on an SDR one, from the picture's own peak. Research log, 2026-10-02 to 2026-10-05.
    /// </para>
    /// </summary>
    public bool RequestHdr { get; set; }

    public LatencyMode LatencyMode { get; set; } = LatencyMode.Balanced;

    public UpscaleMode UpscaleMode { get; set; } = UpscaleMode.None;

    /// <summary>
    /// Show the bandwidth controller's recommendation in the diagnostics panel. Named for what it once meant to do:
    /// nothing acts on the recommendation yet, so it changes no stream (2026-10-02; ROADMAP has making a reconnect
    /// start from it). The console adjusts its own bitrate regardless.
    /// </summary>
    public bool AdaptiveQuality { get; set; } = true;

    /// <summary>
    /// Send our measured link quality and desired bitrate to the console (CONNECTION_QUALITY). Kept so a stored
    /// value still reads, but no longer acted on and no longer offered: on 2026-10-02 the console did not follow the
    /// target in kbps or in bps (research log). <see cref="ToSessionConfig"/> always sends false.
    /// </summary>
    public bool ReportConnectionQuality { get; set; }

    /// <summary>The <em>default</em> answer for the disconnect prompt's rest-mode checkbox — and, when
    /// <see cref="ConfirmOnDisconnect"/> is off, the choice applied outright. Off by default, matching the
    /// vendor's disconnect checkbox defaulting to "keep on".</summary>
    public bool RestConsoleOnDisconnect { get; set; }

    /// <summary>Ask before ending a stream, with a rest-mode choice, rather than disconnecting immediately.
    /// On by default: a disconnect is destructive of a live session and the rest choice is worth surfacing.
    /// Turn off to leave instantly using <see cref="RestConsoleOnDisconnect"/> as the standing answer.</summary>
    public bool ConfirmOnDisconnect { get; set; } = true;

    /// <summary>Windows' navigation sounds while a controller is in use (never for the mouse or keyboard). On by
    /// default, the console convention; a switch because a sound is the one thing a player can't look away from.</summary>
    public bool NavigationSounds { get; set; } = true;

    // ---- device ----

    public GpuPreference GpuPreference { get; set; } = GpuPreference.Auto;

    /// <summary>Adapter LUID, used only when <see cref="GpuPreference"/> is <see cref="GpuPreference.Specific"/>.</summary>
    public ulong GpuLuid { get; set; }

    // ---- input ----

    public ExitGesture ExitGesture { get; set; } = ExitGesture.StartSelectShoulders;

    /// <summary>
    /// The first-run setup this install has finished, or skipped: 0 for none. The setup shows while it is behind
    /// the current one (SetupFlow.CurrentVersion, in the presentation layer).
    /// </summary>
    public int SetupVersion { get; set; }

    /// <summary>The consoles page's "Xbox controllers need GameInput" notice was closed, so it stays closed.</summary>
    public bool GameInputNoticeClosed { get; set; }

    /// <summary>
    /// Keyboard bindings and gamepad button remap. Persisted so a remap for exotic hardware survives a restart,
    /// which is the entire point of having one — the alternative was a code change per device.
    /// </summary>
    public InputBindings InputBindings { get; set; } = new();

    /// <summary>Stick deflection that counts as a direction when navigating the app's own UI.</summary>
    public double UiStickDeadzone { get; set; } = 0.5;

    // ---- presentation / diagnostics ----

    /// <summary>Enter fullscreen automatically when a stream starts.</summary>
    public bool FullScreenOnConnect { get; set; } = true;

    /// <summary>
    /// Show the diagnostics overlay from the start. Default false — it used to be visible by default, printing
    /// live stick coordinates over the game.
    ///
    /// <para>
    /// <b>Superseded by <see cref="DiagnosticsOnConnect"/>, and kept anyway.</b> It is what a settings file
    /// written by an older build carries, and it is still written by this one, so a user who moves between
    /// builds does not lose the preference in either direction. Read it through
    /// <see cref="DiagnosticsRungOnConnect"/> rather than directly.
    /// </para>
    /// </summary>
    public bool ShowDiagnosticsOverlay { get; set; }

    /// <summary>
    /// How much of the HUD a stream opens at.
    ///
    /// <para>
    /// <b>Nullable, and that is the migration.</b> This could not simply replace the bool above: the settings
    /// store catches <c>JsonException</c> and falls back to defaults, so a file carrying
    /// <c>"ShowDiagnosticsOverlay": false</c> against a property that had become an enum would not lose one
    /// preference — it would silently lose all of them. Null therefore means "no answer in the file", which is
    /// distinguishable from an answer of <see cref="DiagnosticsRung.Hidden"/>, and lets the old bool supply
    /// one. It stops being null the first time the user touches the setting.
    /// </para>
    /// </summary>
    public DiagnosticsRung? DiagnosticsOnConnect { get; set; }

    /// <summary>
    /// The rung a stream should open at: this build's answer if the file has one, otherwise the older build's
    /// bool, otherwise hidden.
    /// </summary>
    [JsonIgnore]
    public DiagnosticsRung DiagnosticsRungOnConnect =>
        DiagnosticsOnConnect ?? (ShowDiagnosticsOverlay ? DiagnosticsRung.Summary : DiagnosticsRung.Hidden);

    /// <summary>
    /// Set the rung, keeping the legacy bool in step so an older build reading this file still opens the HUD
    /// when the user asked for it.
    /// </summary>
    public RipcordSettings WithDiagnosticsRung(DiagnosticsRung rung)
    {
        RipcordSettings updated = this with
        {
            DiagnosticsOnConnect = rung,
            ShowDiagnosticsOverlay = rung != DiagnosticsRung.Hidden,
        };

        return updated;
    }

    /// <summary>Larger text and controls, for handhelds and TV viewing distances.</summary>

    /// <summary>Build the session configuration these settings describe.</summary>
    public SessionConfig ToSessionConfig()
        => new(
            Width,
            Height,
            TargetFps,
            BitrateKbps,
            Codec,
            LatencyMode,
            // Never from the stored value: the console ignored the report's target in kbps and in bps (2026-10-02,
            // research log), so the Settings switch was taken out, and an install that had turned it on must not go
            // on sending with nothing left to turn it off.
            ReportConnectionQuality: false,
            // HDR is gated on HEVC rather than trusted from settings: an 8-bit AVC stream cannot carry it, and
            // asking for a combination the console cannot serve risks it declining the whole launchSpec.
            RequestHdr && Codec == VideoCodec.Hevc ? DynamicRange.Hdr : DynamicRange.Sdr,
            RestConsoleOnDisconnect);
}
