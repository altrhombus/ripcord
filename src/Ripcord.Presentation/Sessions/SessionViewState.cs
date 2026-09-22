using Ripcord.Core.Sessions;
using Ripcord.Presentation.Resources;

namespace Ripcord.Presentation.Sessions;

/// <summary>
/// How a pad is attached. A portable mirror of the input stack's own transport enum, which lives in a
/// Windows-only assembly — the same "front end maps its types onto ours" pattern as <c>AccentRole</c>.
/// </summary>
public enum ControllerLink
{
    Unknown,
    Usb,
    Bluetooth,
}

/// <summary>
/// What route, if any, the rung-1 notice may offer for going a rung deeper.
///
/// <para>
/// Portable and semantic, never a key name: the notice used to end in a literal "F3" pill whatever the player
/// had in their hands, which on a handheld names a key that is not on the device. Which glyph or word each of
/// these becomes is the front end's business, and the front end already tracks the input mode for its hint
/// bar — this only says whether a route exists.
/// </para>
/// </summary>
public enum AlertHint
{
    /// <summary>
    /// Say nothing. The pad case: nothing in the stream layer takes gamepad input by design, so every route
    /// out of rung 1 is one a pad cannot walk, and naming one would be worse than silence.
    /// </summary>
    None,

    /// <summary>A keyboard shortcut is available. The front end names the key.</summary>
    Key,

    /// <summary>The notice itself is the control. Touch, where a target you can hit beats a key you cannot.</summary>
    Tap,
}

/// <summary>One capability worth advertising about the picture currently on screen.</summary>
/// <param name="Accent">
/// Whether it is the headline capability rather than a supporting one. The <em>emphasis</em>, not the colour —
/// which brush that becomes is the front end's business.
/// </param>
public readonly record struct CapabilityPill(string Label, bool Accent);

/// <summary>
/// Everything the streaming surface shows, as one value.
///
/// <para>
/// Split into the overlay and the diagnostics panel because the two change at completely different rates: the
/// overlay changes a handful of times per session, the panel twice a second. Keeping them as one flat record
/// would make every stats tick recompose the overlay's strings as well, and — worse for reading the code —
/// would put "why can't I connect" and "how fast is the decoder" at the same altitude.
/// </para>
/// </summary>
public sealed record SessionViewState(
    bool StatusVisible,
    string StatusHeadline,
    string StatusDetail,

    /// <summary>Whether to show a busy indicator: something is still in progress, so waiting is the right thing.</summary>
    bool StatusBusy,

    /// <summary>
    /// Whether to offer the retry/leave actions. True exactly when the status is terminal — a state the session
    /// will not leave on its own, so the user has to choose something.
    /// </summary>
    bool StatusActionsVisible,

    /// <summary>
    /// The session is live and a picture is arriving. The front end's cue to go immersive; kept as state rather
    /// than an event so a surface that attaches late still knows where it stands.
    /// </summary>
    bool IsStreamLive,

    /// <summary>
    /// Rung 1 of the HUD: something is wrong and has stayed wrong long enough to be worth saying over a
    /// running game. Gated by <see cref="HealthAlertGate"/> rather than read straight off the verdict, and
    /// suppressed both while the status overlay is up and once the HUD itself is open, since each of those
    /// already says the same thing in more words.
    /// </summary>
    bool AlertVisible,

    /// <summary>
    /// Whether the notice may offer a way deeper, and by what route. See <see cref="Sessions.AlertHint"/>.
    /// </summary>
    AlertHint AlertHint,

    /// <summary>How much of the HUD is on screen. See <see cref="DiagnosticsRung"/>.</summary>
    DiagnosticsRung Rung,

    /// <summary>
    /// How far the connect sequence has got, for the trail. <see langword="null"/> whenever the status on
    /// screen did not come from that sequence — a reconnect, a stall, a close — because a trail that keeps
    /// its last position through an unrelated state is claiming progress it does not have.
    /// </summary>
    ConnectPhase? Phase,

    /// <summary>
    /// Which pads are attached, one per line. At the top level rather than inside the diagnostics record because
    /// it changes when hardware is plugged in, not on the twice-a-second stats cadence.
    /// </summary>
    string ConnectedControllers,

    SessionDiagnosticsState Diagnostics)
{
    public static SessionViewState Initial { get; } = new(
        StatusVisible: true,
        StatusHeadline: Strings.Session_Starting,
        StatusDetail: string.Empty,
        StatusBusy: true,
        StatusActionsVisible: false,
        IsStreamLive: false,
        AlertVisible: false,
        AlertHint: AlertHint.Key,
        Rung: DiagnosticsRung.Hidden,
        Phase: null,
        ConnectedControllers: Strings.Session_NoControllers,
        Diagnostics: SessionDiagnosticsState.Empty);
}

/// <summary>
/// The diagnostics overlay's readout.
///
/// <para>
/// Every field is a finished string, because every one of them was a finished string built inline in the page's
/// stats tick — roughly 150 lines of formatting, arithmetic and threshold judgement that could only be exercised
/// by connecting to a real console with a real GPU. None of it needs either.
/// </para>
/// </summary>
public sealed record SessionDiagnosticsState(
    // ---- device ----
    string Adapter,

    // ---- video ----
    string Colour,
    string VideoFormat,
    string HdrOutput,
    string HeroResolution,
    string HeroFps,
    string HeroBitrate,
    IReadOnlyList<CapabilityPill> CapabilityPills,

    /// <summary>
    /// A stable identity for the pill set, so a front end that has to build elements for each one can skip the
    /// rebuild when nothing has changed. Rebuilding twice a second churns layout for no visible difference.
    /// </summary>
    string CapabilityPillSignature,

    string Decoder,

    // ---- target ----
    string Requested,
    string Adaptive,
    bool AdaptiveVisible,
    string Reason,
    bool ReasonVisible,

    // ---- link ----
    string Link,

    /// <summary>Fraction of the configured bitrate cap currently in use, clamped to 0..1.</summary>
    double HeadroomUsedFraction,
    string Headroom,
    string Power,

    // ---- pipeline ----
    string Audio,
    string Decode,
    string Queues,
    string Path,

    // ---- verdict ----
    string Health,
    string HealthTip,

    /// <summary>
    /// The rung-1 line: the same verdict as <see cref="Health"/>, plus the single clause a player can act on.
    /// Separate from the headline so all three rungs still say the same sentence — see
    /// <see cref="StreamHealthVerdict.Notice"/>.
    /// </summary>
    string HealthNotice,
    StreamHealthLevel HealthLevel,

    /// <summary>Loss as a percentage, for rung 2. The panel plots it; the summary strip states it.</summary>
    string HeroLoss,

    /// <summary>
    /// The decoded picture size as numbers rather than as the display string beside it. The front end has
    /// to compute a letterbox from it — see <see cref="HudPlacement"/> — and parsing that back out of
    /// "1920×1080" would be inventing a format to re-read.
    /// </summary>
    int VideoWidth,
    int VideoHeight,

    // ---- instrument severities ----

    /// <summary>
    /// How each plotted metric stands against its own threshold, so a stroke's colour carries information
    /// rather than merely identifying which row it belongs to. Bitrate has no severity by construction —
    /// there is no bitrate that is wrong by itself. See <see cref="MetricPlot"/>.
    /// </summary>
    MetricSeverity FramesSeverity,
    MetricSeverity LatencySeverity,
    MetricSeverity LossSeverity)
{
    public static SessionDiagnosticsState Empty { get; } = new(
        Adapter: "—",
        Colour: "—",
        VideoFormat: "—",
        HdrOutput: "—",
        HeroResolution: "—",
        HeroFps: "0",
        HeroBitrate: "—",
        CapabilityPills: [],
        CapabilityPillSignature: string.Empty,
        Decoder: "—",
        Requested: string.Empty,
        Adaptive: string.Empty,
        AdaptiveVisible: false,
        Reason: string.Empty,
        ReasonVisible: false,
        Link: "—",
        HeadroomUsedFraction: 0,
        Headroom: string.Empty,
        Power: string.Empty,
        Audio: string.Empty,
        Decode: string.Empty,
        Queues: string.Empty,
        Path: string.Empty,
        Health: Strings.Session_NotConnectedYet,
        HealthTip: Strings.Session_WaitingToStart,
        HealthNotice: Strings.Session_NotConnectedYet,
        HealthLevel: StreamHealthLevel.Info,
        HeroLoss: "—",
        VideoWidth: 0,
        VideoHeight: 0,
        FramesSeverity: MetricSeverity.Normal,
        LatencySeverity: MetricSeverity.Normal,
        LossSeverity: MetricSeverity.Normal);
}
