using Ripcord.Core.Sessions;

namespace Ripcord.Presentation.Sessions;

/// <summary>
/// One tick's worth of facts from the session controller and the power monitor.
///
/// <para>
/// Passed in rather than reached for through a seam, and the distinction is deliberate: a decode pipeline is a
/// <em>device</em>, which is why <see cref="IVideoPipelineStats"/> exists, whereas all of this is already plain
/// data that the portable layer could read directly from <c>SessionController</c>. Taking it as a parameter
/// instead of holding a controller reference means the view-model has no lifecycle to manage and a test needs no
/// substitute controller — it writes down the numbers it wants to reason about.
/// </para>
/// </summary>
/// <param name="HasSession">
/// False before the controller exists. Worth its own flag rather than inferring it from zeroed statistics,
/// because "frame rate is below target" is a misleading thing to say about a stream that has not started.
/// </param>
public sealed record SessionTelemetry(
    bool HasSession,
    SessionStatistics Statistics,
    BitrateDecision? RecommendedQuality = null,
    string QualityReason = "",
    double MillisecondsSinceConnect = 0,
    double? MillisecondsSinceLastFrame = null,
    PowerState? Power = null)
{
    /// <summary>Nothing connected yet — the state the overlay spends its first few seconds in.</summary>
    public static SessionTelemetry None { get; } =
        new(HasSession: false, Statistics: new SessionStatistics(0, 0, 0, 0, 0));
}
