using Ripcord.Core.Sessions;

namespace Ripcord.Presentation.Sessions;

/// <summary>
/// One tick's worth of facts from the session controller and the power monitor.
///
/// <para>
/// Passed in rather than reached for through a seam: a decode pipeline is a <em>device</em>, which is why
/// <see cref="IVideoPipelineStats"/> exists, whereas this is plain data. Taking it as a parameter instead of
/// holding a <c>SessionController</c> reference means the view-model has no lifecycle to manage and a test
/// needs no substitute controller.
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
