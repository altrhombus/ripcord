namespace Ripcord.Presentation.Setup;

/// <summary>The setup's steps, in order. A <see cref="SetupScope.PictureOnly"/> setup has only the picture.</summary>
public enum SetupStep
{
    Welcome,
    Picture,
    Console,
    Controller,
    Done,
}

/// <summary>How much of the setup to show.</summary>
public enum SetupScope
{
    /// <summary>Everything: a first run, or "Run setup again" from Settings.</summary>
    Full,

    /// <summary>
    /// The picture choice alone, once, for an install that already has a console: it was set up before the setup
    /// existed, and the one thing it missed is the chance to choose HEVC and HDR.
    /// </summary>
    PictureOnly,
}

/// <summary>The two ways to stream the setup offers.</summary>
public enum PictureChoice
{
    /// <summary>HEVC with HDR: the better picture, where the PC can decode HEVC.</summary>
    BestPicture,

    /// <summary>H.264 without HDR, which every PC can decode.</summary>
    MostCompatible,
}

/// <summary>
/// Everything the setup page shows, as one value. All of the text is composed here, in the catalogue, so the page
/// has nothing to decide.
/// </summary>
public sealed record SetupFlowState(
    SetupStep Step,
    SetupScope Scope,
    string Title,

    // How many of the three step dashes are lit (picture, console, controller); none on a picture-only setup.
    int ReachedDash,
    bool ShowsDashes,

    // ---- picture ----
    string PictureIntro,
    // True while the PC is being checked for HEVC and an HDR display. Nothing can be chosen yet.
    bool Checking,
    bool BestPictureAvailable,
    PictureChoice Choice,
    string BestPictureLabel,
    string BestPictureDetail,
    string CompatibleLabel,
    string CompatibleDetail,
    string RecommendedLabel,
    // Why the better picture isn't offered, or empty.
    string PictureNote,
    // The resolution, frame rate and bitrate the choice comes with.
    string PictureDefaults,

    // ---- console ----
    string ConsoleIntro,
    // What to switch on at the console first, so pairing doesn't fail on a setting: a line that introduces it,
    // then the settings as numbered steps.
    string ConsolePreflight,
    IReadOnlyList<PreflightStep> ConsolePreflightSteps,
    bool ConsoleAdded,
    // The console just paired, or empty.
    string ConsoleStatus,

    // ---- controller ----
    string ControllerIntro,
    bool ControllerSeen,
    string ControllerStatus,
    // A second line under the status, or empty.
    string ControllerDetail,
    // That Xbox controllers need GameInput, when it isn't installed; empty otherwise.
    string GameInputNote,
    string ExitGestureLine,

    // ---- welcome and done ----
    string WelcomeBody,
    string DonePicture,
    string DoneConsole,
    string DoneController,

    // ---- footer ----
    string PrimaryLabel,
    bool PrimaryEnabled,
    // Empty hides the button.
    string SecondaryLabel,
    bool CanGoBack,
    string BackLabel);

/// <summary>
/// One thing to switch on at the console: what to turn on, and where the console keeps it.
///
/// <para>
/// A list rather than a sentence. The console-side settings are what most often stop a first pairing, and they were
/// one run-on paragraph in small grey caption text (visual audit, 2026-10-08), so the steps are separate and the
/// menu path sits apart from the thing to turn on.
/// </para>
/// </summary>
public sealed record PreflightStep(string TurnOn, string Where);
