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
    StreamHealthLevel HealthLevel)
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
        HealthLevel: StreamHealthLevel.Info);
}
