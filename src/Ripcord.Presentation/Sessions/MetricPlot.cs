namespace Ripcord.Presentation.Sessions;

/// <summary>
/// How one instrument reading stands against its own threshold.
///
/// <para>
/// Distinct from <see cref="Ripcord.Core.Sessions.StreamHealthLevel"/>, which is a verdict about the whole
/// stream and carries an <c>Info</c> case that means "worth knowing, not wrong". A single metric has no such
/// state: it is inside its threshold or it is past it.
/// </para>
/// </summary>
public enum MetricSeverity
{
    /// <summary>Inside its threshold. Draws neutral — see the note on <see cref="MetricPlot"/>.</summary>
    Normal,

    /// <summary>Past the warn threshold.</summary>
    Warning,

    /// <summary>Past the bad threshold, where one exists.</summary>
    Critical,
}

/// <summary>
/// Where one sparkline's axis sits: what value is at the top, and where its threshold rule is drawn.
///
/// <para>
/// <b>Why this exists at all.</b> The four sparkline strokes used to be fixed per metric in markup — frames
/// always success-green, latency always caution-yellow, loss always critical-red. So the loss chart was red
/// when loss was zero and the frames chart was green while frames collapsed: colour that looked like it
/// carried meaning and carried none, in an app whose standing rule is that colour is never the sole carrier
/// of it. A stroke now takes its colour from <see cref="MetricSeverity"/>, which means a coloured line is
/// always a line worth looking at.
/// </para>
///
/// <para>
/// <b>Scales stay fixed.</b> An auto-scaled axis makes a calm stream and a broken one look identical, which
/// is the whole reason these are declared rather than derived from the data.
/// </para>
/// </summary>
/// <param name="FullScale">The value at the top of the plot. Never derived from the samples.</param>
/// <param name="WarnFraction">
/// Where to draw the threshold rule, 0 at the bottom and 1 at the top; null when the metric has no threshold
/// worth drawing. <b>1 is the common case and is not a degenerate one:</b> loss and latency are scaled so that
/// full scale IS the warn threshold, so their rule sits along the ceiling and "touching the top" reads as
/// "this is now a problem". Drawing it makes that explicit instead of folklore.
/// </param>
public sealed record MetricPlot(double FullScale, double? WarnFraction);
