using Ripcord.Presentation.Consoles;

namespace Ripcord.Presentation.Settings;

/// <summary>Whether one HDR prerequisite is met, unmet, or not knowable yet.</summary>
public enum HdrCheckState
{
    Met,
    Unmet,

    /// <summary>
    /// Not answerable here. The console's own HDR output cannot be known until a session runs, and a cross for
    /// "unknown" would read as a fault the user could go and fix.
    /// </summary>
    Unknown,
}

/// <summary>
/// One row of the HDR readiness checklist.
/// </summary>
/// <param name="Label">
/// Names the REQUIREMENT and stays constant; the state carries whether it is met. Phrasing these as findings
/// instead ("Display is not in HDR mode") made the text and the icon restate each other, and read oddly against
/// a tick — a row cannot both assert a state and be marked true or false.
/// </param>
/// <param name="Hint">The remedy, or null when the row is met and there is nothing to do.</param>
public sealed record HdrCheck(HdrCheckState State, string Label, string? Hint);

/// <summary>
/// Everything the settings page shows.
///
/// <para>
/// Flat and long, because the page is. What matters is that it is <em>one value</em>: the page projects it whole,
/// so there is no ordering in which half the controls reflect the new settings and half the old — which is what
/// the codec/HDR pair used to get wrong when a change handler updated one and left the other for the next visit.
/// </para>
/// </summary>
public sealed record SettingsViewState(
    // ---- video ----
    IReadOnlyList<string> ResolutionOptions,
    int ResolutionIndex,
    double BitrateMbps,
    string BitrateLabel,
    int UpscaleIndex,
    bool AdaptiveQuality,
    bool ReportConnectionQuality,

    // ---- codec and HDR ----
    IReadOnlyList<string> CodecOptions,
    int CodecIndex,
    bool CodecPickerEnabled,
    string CodecHelp,
    bool RequestHdr,
    bool HdrToggleEnabled,
    IReadOnlyList<HdrCheck> HdrChecks,
    string HdrHelp,

    // ---- gpu ----
    int GpuPreferenceIndex,
    bool AdapterPickerVisible,
    IReadOnlyList<string> AdapterOptions,
    int AdapterIndex,
    string AdapterWarning,
    bool AdapterWarningVisible,

    // ---- input ----
    bool KeyboardEnabled,
    string KeyboardSummary,
    int ExitGestureIndex,
    string ExitGestureDescription,
    double UiStickDeadzone,
    string DeadzoneLabel,

    // ---- session behaviour ----
    bool FullScreenOnConnect,
    bool ConfirmOnDisconnect,
    bool RestConsoleOnDisconnect,
    /// <summary>
    /// Which rung a stream opens at, as an index into <see cref="DiagnosticsOptions"/>. A picker rather
    /// than a switch because there are three answers, and because it is the only control a pad-only player
    /// has over the HUD — nothing in the stream layer takes gamepad input, by design.
    /// </summary>
    IReadOnlyList<string> DiagnosticsOptions,
    int DiagnosticsIndex,
    bool LargeUiScale,

    // ---- credential banner ----
    string CredentialTitle,
    string CredentialMessage,
    StatusTone CredentialTone,

    /// <summary>
    /// Set when something failed to load. Shown in place of the credential banner, because this page is also
    /// where a user goes to fix a bad configuration and must stay reachable and honest about what it could not
    /// read.
    /// </summary>
    string? LoadError);
