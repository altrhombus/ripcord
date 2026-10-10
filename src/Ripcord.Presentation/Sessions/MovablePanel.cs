using Ripcord.Core.Settings;

namespace Ripcord.Presentation.Sessions;

/// <summary>Which edges of a panel a resize is pulling.</summary>
[Flags]
public enum PanelEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

/// <summary>A panel's rectangle in the window, in effective pixels.</summary>
public readonly record struct PanelRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

/// <summary>
/// The arithmetic of a panel the player can move and resize: the full diagnostics panel (owner, 2026-10-09).
///
/// <para>
/// It goes where <see cref="HudPlacement"/> puts it until it is moved; after that it sits where it was put, at the
/// size it was given, and stays wholly inside the window however the window changes. The position is kept as a
/// fraction of the room beside it (<see cref="PanelFrame"/>), so a panel put in the right-hand corner is still in
/// it when the window is resized or the stream goes full screen. Pure, so the clamping is tested here and the page
/// only turns pointer moves into calls.
/// </para>
/// </summary>
public static class MovablePanel
{
    /// <summary>Narrower than this and the panel's labels wrap into their own values.</summary>
    public const double MinimumWidth = 280;

    /// <summary>Shorter than this and the body is a sliver under the header.</summary>
    public const double MinimumHeight = 160;

    /// <summary>How far in from a panel's edge a pointer still takes hold of that edge.</summary>
    public const double ResizeBand = 8;

    /// <summary>
    /// A panel at least this wide, and much wider than it is tall, lays its groups out side by side, as the sheet in
    /// a letterbox bar does; anything else is the column.
    /// </summary>
    public const double RowMinimumWidth = 900;

    /// <summary>Where a saved panel goes in a viewport of this size: its size kept where it fits, wholly inside.</summary>
    public static PanelRect Place(PanelFrame frame, double viewportWidth, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(frame);

        double width = Fit(frame.Width, MinimumWidth, viewportWidth);
        double height = Fit(frame.Height, MinimumHeight, viewportHeight);
        return new PanelRect(
            Unit(frame.AcrossX) * Math.Max(0, viewportWidth - width),
            Unit(frame.AcrossY) * Math.Max(0, viewportHeight - height),
            width,
            height);
    }

    /// <summary>What to save for a panel at <paramref name="rect"/> in a viewport of this size.</summary>
    public static PanelFrame ToFrame(PanelRect rect, double viewportWidth, double viewportHeight)
    {
        double roomX = viewportWidth - rect.Width;
        double roomY = viewportHeight - rect.Height;
        return new PanelFrame(
            rect.Width,
            rect.Height,
            roomX > 0 ? Unit(rect.Left / roomX) : 0,
            roomY > 0 ? Unit(rect.Top / roomY) : 0);
    }

    /// <summary>A panel dragged by (<paramref name="dx"/>, <paramref name="dy"/>) from where the drag began.</summary>
    public static PanelRect Move(PanelRect start, double dx, double dy, double viewportWidth, double viewportHeight)
        => start with
        {
            Left = Math.Clamp(start.Left + dx, 0, Math.Max(0, viewportWidth - start.Width)),
            Top = Math.Clamp(start.Top + dy, 0, Math.Max(0, viewportHeight - start.Height)),
        };

    /// <summary>
    /// A panel resized by pulling <paramref name="edges"/> by (<paramref name="dx"/>, <paramref name="dy"/>) from where
    /// the drag began. The opposite edges stay put; the pulled ones stop at the window and at the minimum size.
    /// </summary>
    public static PanelRect Resize(
        PanelRect start, PanelEdges edges, double dx, double dy, double viewportWidth, double viewportHeight)
    {
        double minWidth = Math.Min(MinimumWidth, viewportWidth);
        double minHeight = Math.Min(MinimumHeight, viewportHeight);
        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;

        if (edges.HasFlag(PanelEdges.Left))
        {
            left = Math.Clamp(start.Left + dx, 0, Math.Max(0, right - minWidth));
        }
        else if (edges.HasFlag(PanelEdges.Right))
        {
            right = Math.Clamp(start.Right + dx, left + minWidth, Math.Max(left + minWidth, viewportWidth));
        }

        if (edges.HasFlag(PanelEdges.Top))
        {
            top = Math.Clamp(start.Top + dy, 0, Math.Max(0, bottom - minHeight));
        }
        else if (edges.HasFlag(PanelEdges.Bottom))
        {
            bottom = Math.Clamp(start.Bottom + dy, top + minHeight, Math.Max(top + minHeight, viewportHeight));
        }

        return new PanelRect(left, top, right - left, bottom - top);
    }

    /// <summary>Which edges a pointer at (<paramref name="x"/>, <paramref name="y"/>), relative to the panel, takes hold of.</summary>
    public static PanelEdges EdgesAt(double width, double height, double x, double y)
    {
        PanelEdges edges = PanelEdges.None;
        if (x <= ResizeBand)
        {
            edges |= PanelEdges.Left;
        }
        else if (x >= width - ResizeBand)
        {
            edges |= PanelEdges.Right;
        }

        if (y <= ResizeBand)
        {
            edges |= PanelEdges.Top;
        }
        else if (y >= height - ResizeBand)
        {
            edges |= PanelEdges.Bottom;
        }

        return edges;
    }

    /// <summary>Whether a panel of this size lays its groups out as a row rather than a column.</summary>
    public static bool ArrangesAsRow(double width, double height)
        => width >= RowMinimumWidth && width >= height * 2.5;

    private static double Fit(double value, double minimum, double viewport)
    {
        double floor = Math.Min(minimum, viewport);
        return double.IsFinite(value) ? Math.Clamp(value, floor, Math.Max(floor, viewport)) : floor;
    }

    private static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
