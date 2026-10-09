using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace Ripcord_App.Services;

/// <summary>
/// The three-dash step trail on Add a console and the first-run setup, painted for where the reader has got to.
///
/// <para>
/// <b>Why it is painted in code.</b> The trail says progress by dimming the steps not yet reached, and dimming is
/// exactly what a high-contrast theme takes away: the dashes are accent-filled, and in a contrast theme they
/// disappeared outright, reached and unreached alike, so the one thing the trail carries was gone (visual audit,
/// 2026-10-08). Contrast themes get the shape instead of the shade: a reached step is filled in the theme's
/// highlight colour, a step to come is an outline in its text colour, and nothing is translucent.
/// </para>
///
/// <para>
/// Outside high contrast everything set here is cleared, so the dashes fall back to RipcordStepDashStyle and its
/// theme-following accent fill.
/// </para>
/// </summary>
internal static class StepDashPainter
{
    /// <summary>How visible a step not yet reached is, outside high contrast.</summary>
    private const double Unreached = 0.2;

    public static void Paint(int reached, params Rectangle[] dashes)
    {
        bool contrast = AppEffects.HighContrast;
        UISettings? colours = contrast ? new UISettings() : null;

        for (int i = 0; i < dashes.Length; i++)
        {
            Rectangle dash = dashes[i];
            bool done = i + 1 <= reached;

            if (colours is null)
            {
                dash.ClearValue(Shape.FillProperty);
                dash.ClearValue(Shape.StrokeProperty);
                dash.ClearValue(Shape.StrokeThicknessProperty);
                dash.Opacity = done ? 1.0 : Unreached;
                continue;
            }

            var highlight = new SolidColorBrush(colours.UIElementColor(UIElementType.Highlight));
            var text = new SolidColorBrush(colours.UIElementColor(UIElementType.WindowText));

            dash.Opacity = 1.0;
            dash.Fill = done ? highlight : null;
            dash.Stroke = done ? highlight : text;
            dash.StrokeThickness = 1;
        }
    }
}
