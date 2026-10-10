namespace Ripcord.Presentation.Sessions;

/// <summary>Where the full instrument panel sits, given how the video fits the window.</summary>
public enum DiagnosticsPlacement
{
    /// <summary>
    /// In the pillarbox beside the picture. Nothing is covered, so it can stay up indefinitely.
    /// </summary>
    Rail,

    /// <summary>In the letterbox bar beneath the picture. Also free.</summary>
    Sheet,

    /// <summary>Over the picture, because there is no dead space to claim.</summary>
    Overlay,
}

/// <summary>
/// The panel's placement and the dead space it has to work with.
/// </summary>
/// <param name="Placement">Where it goes.</param>
/// <param name="PillarWidth">Width of ONE pillarbox bar, in the same units as the viewport.</param>
/// <param name="BarHeight">Height of ONE letterbox bar.</param>
public readonly record struct HudLayout(DiagnosticsPlacement Placement, double PillarWidth, double BarHeight);

/// <summary>
/// Decides where the full instrument panel goes.
///
/// <para>
/// <b>The panel claims dead space first and overlays only when there is none.</b> The stream is 16:9 and the
/// window usually is not, so there is nearly always a bar somewhere that is already black and already
/// carrying nothing — an ultrawide leaves 440 px down each side of a 16:9 picture. Putting the instruments
/// there costs the player no picture at all, which also answers why nobody keeps the panel open today: it is
/// expensive to, and it does not have to be.
/// </para>
///
/// <para>
/// The arithmetic is already being done — the swap chain knows the video size and the panel knows its bounds
/// — so this is a decision, not a measurement. Pure and unit-tested, because the alternative is discovering
/// on an ultrawide that the rule was written for one aspect ratio.
/// </para>
/// </summary>
public static class HudPlacement
{
    /// <summary>
    /// Narrowest pillar the rail will accept. The rail is a column of label/value rows and four sparklines;
    /// below this it would wrap its own labels, at which point the overlay is the better answer because at
    /// least it is legible. A 3440×1440 display leaves 440 per side and a 2560×1080 leaves 320, so the
    /// common ultrawides clear it and a merely-wide desktop window does not.
    /// </summary>
    public const double RailMinimumWidth = 300;

    /// <summary>
    /// Shortest letterbox bar the sheet will accept. Roughly what the four column groups need before they
    /// start scrolling, which is the thing the sheet exists to avoid. A 1280×800 handheld shows a 16:9
    /// picture at 1280×720, leaving 40 px at each end, so it overlays — correctly, because forty pixels of
    /// instruments would be neither readable nor free.
    /// </summary>
    public const double SheetMinimumHeight = 200;

    /// <summary>
    /// Where the panel goes for a given viewport and video size.
    ///
    /// <para>
    /// <b>Only one bar can exist at a time.</b> The picture is scaled uniformly to fit, so whichever axis is
    /// the tighter constraint fills the viewport exactly and the other is where the slack goes — a window is
    /// pillarboxed or letterboxed, never both. The order the two are tested in is therefore not a preference
    /// between them, and writing it as one would invite someone to "fix" a tie that cannot happen.
    /// </para>
    /// </summary>
    /// <param name="viewportWidth">Width available to the stream layer.</param>
    /// <param name="viewportHeight">Height available to the stream layer.</param>
    /// <param name="videoWidth">Decoded picture width. Zero while no frame has arrived.</param>
    /// <param name="videoHeight">Decoded picture height.</param>
    public static HudLayout For(
        double viewportWidth, double viewportHeight, double videoWidth, double videoHeight)
    {
        // No picture yet, or a degenerate viewport: there is no letterbox to reason about, and guessing at
        // one would move the panel the moment the first frame disagreed.
        if (!IsPositive(viewportWidth) || !IsPositive(viewportHeight)
            || !IsPositive(videoWidth) || !IsPositive(videoHeight))
        {
            return new HudLayout(DiagnosticsPlacement.Overlay, 0, 0);
        }

        // The picture is scaled to fit, preserving aspect — so one axis fills the viewport and the other
        // leaves an equal bar at each end.
        double scale = Math.Min(viewportWidth / videoWidth, viewportHeight / videoHeight);
        double shownWidth = videoWidth * scale;
        double shownHeight = videoHeight * scale;

        double pillar = Math.Max(0, (viewportWidth - shownWidth) / 2);
        double bar = Math.Max(0, (viewportHeight - shownHeight) / 2);

        DiagnosticsPlacement placement = pillar >= RailMinimumWidth
            ? DiagnosticsPlacement.Rail
            : bar >= SheetMinimumHeight
                ? DiagnosticsPlacement.Sheet
                : DiagnosticsPlacement.Overlay;

        return new HudLayout(placement, pillar, bar);
    }

    private static bool IsPositive(double value) => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
}
