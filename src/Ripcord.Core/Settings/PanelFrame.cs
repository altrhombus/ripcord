namespace Ripcord.Core.Settings;

/// <summary>
/// A panel the player has moved or resized: its size, and where it sits in the room the window leaves around it.
/// </summary>
/// <param name="Width">The panel's width, in effective pixels.</param>
/// <param name="Height">The panel's height, in effective pixels.</param>
/// <param name="AcrossX">
/// Where it sits across the room the window leaves beside it: 0 against the left edge, 1 against the right. A fraction
/// rather than a position, so a panel put in a corner stays in that corner when the window changes size.
/// </param>
/// <param name="AcrossY">The same, top to bottom.</param>
public sealed record PanelFrame(double Width, double Height, double AcrossX, double AcrossY);
