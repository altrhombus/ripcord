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

    /// <summary>Requested stream width. 1280x720 is the verified-good default; 1080p is selectable.</summary>
    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 720;

    public int TargetFps { get; set; } = 60;

    /// <summary>Requested bitrate. The adaptive controller may reduce this at runtime.</summary>
    public int BitrateKbps { get; set; } = 10_000;

    public VideoCodec Codec { get; set; } = VideoCodec.H264;

    /// <summary>
    /// Request HDR. Only meaningful with <see cref="VideoCodec.Hevc"/>, since HDR needs a 10-bit stream and the
    /// AVC path the console offers is 8-bit. Off by default: unproven on the wire, and an SDR display would need
    /// tone mapping to show it correctly.
    /// </summary>
    public bool RequestHdr { get; set; }

    public LatencyMode LatencyMode { get; set; } = LatencyMode.Balanced;

    public UpscaleMode UpscaleMode { get; set; } = UpscaleMode.None;

    /// <summary>Let the bandwidth controller reduce quality on loss/thermals. Off pins the requested rate.</summary>
    public bool AdaptiveQuality { get; set; } = true;

    /// <summary>
    /// Send our measured link quality and desired bitrate to the console (CONNECTION_QUALITY). Experimental and
    /// off by default: the message's bitrate units are not wire-confirmed, so it is opt-in until validated
    /// against real hardware.
    /// </summary>
    public bool ReportConnectionQuality { get; set; }

    /// <summary>Put the console into rest mode when a session ends, rather than leaving it awake. Off by
    /// default, matching the vendor's disconnect checkbox defaulting to "keep on".</summary>
    public bool RestConsoleOnDisconnect { get; set; }

    // ---- device ----

    public GpuPreference GpuPreference { get; set; } = GpuPreference.Auto;

    /// <summary>Adapter LUID, used only when <see cref="GpuPreference"/> is <see cref="GpuPreference.Specific"/>.</summary>
    public ulong GpuLuid { get; set; }

    // ---- input ----

    public ExitGesture ExitGesture { get; set; } = ExitGesture.StartSelectShoulders;

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
    /// </summary>
    public bool ShowDiagnosticsOverlay { get; set; }

    /// <summary>Larger text and controls, for handhelds and TV viewing distances.</summary>
    public bool LargeUiScale { get; set; }

    /// <summary>Build the session configuration these settings describe.</summary>
    public SessionConfig ToSessionConfig()
        => new(
            Width,
            Height,
            TargetFps,
            BitrateKbps,
            Codec,
            LatencyMode,
            ReportConnectionQuality,
            // HDR is gated on HEVC rather than trusted from settings: an 8-bit AVC stream cannot carry it, and
            // asking for a combination the console cannot serve risks it declining the whole launchSpec.
            RequestHdr && Codec == VideoCodec.Hevc ? DynamicRange.Hdr : DynamicRange.Sdr,
            RestConsoleOnDisconnect);
}
