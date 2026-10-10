using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ripcord_App.Controls;

/// <summary>
/// A horizontal row that stacks when its items do not fit side by side, rather than cutting the last one off.
///
/// <para>
/// At 225% text in the smallest window, About's links ended in "Prot" and the picture step's "Recommended" pill in
/// "Recommende" (showcase review, T2). WinUI has no wrapping panel and the toolkit's is a package for two rows; a
/// row of two or three items reads as well stacked as wrapped. It answers to the room, so text size and window
/// width both reach it.
/// </para>
/// </summary>
public static class StackWhenTight
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(StackWhenTight), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(StackPanel panel) => (bool)panel.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(StackPanel panel, bool value) => panel.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not StackPanel panel || e.NewValue is not true)
        {
            return;
        }

        panel.Loaded += (_, _) =>
        {
            if (panel.Parent is FrameworkElement parent)
            {
                parent.SizeChanged += (_, _) => Fit(panel, parent);
                Fit(panel, parent);
            }
        };
    }

    private static void Fit(StackPanel panel, FrameworkElement parent)
    {
        double room = parent.ActualWidth - panel.Margin.Left - panel.Margin.Right;
        if (room <= 0)
        {
            return;
        }

        double natural = 0;
        int count = 0;
        foreach (UIElement child in panel.Children.Where(c => c.Visibility == Visibility.Visible))
        {
            child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            natural += child.DesiredSize.Width;
            count++;
        }

        natural += panel.Spacing * System.Math.Max(0, count - 1);

        Orientation wanted = natural > room ? Orientation.Vertical : Orientation.Horizontal;
        if (panel.Orientation != wanted)
        {
            panel.Orientation = wanted;
        }
    }
}
