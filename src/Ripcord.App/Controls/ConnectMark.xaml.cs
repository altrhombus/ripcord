using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ripcord_App.Controls;

/// <summary>
/// The Ripcord mark used as a connect indicator: the wedge lit throughout, and one dash lighting per phase
/// reached. See the XAML for why this replaced three dashes in a row.
/// </summary>
public sealed partial class ConnectMark : UserControl
{
    /// <summary>How visible an unreached dash is. Present, so the shape is whole from the first frame.</summary>
    private const double Unlit = 0.22;

    public ConnectMark()
    {
        InitializeComponent();
    }

    /// <summary>The console's accent, for the trail. Supplied by the caller so high contrast is handled once.</summary>
    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(ConnectMark),
        new PropertyMetadata(null, OnVisualChanged));

    /// <summary>The wedge's colour — the app's own, not a vendor's.</summary>
    public Brush WedgeFill
    {
        get => (Brush)GetValue(WedgeFillProperty);
        set => SetValue(WedgeFillProperty, value);
    }

    public static readonly DependencyProperty WedgeFillProperty = DependencyProperty.Register(
        nameof(WedgeFill),
        typeof(Brush),
        typeof(ConnectMark),
        new PropertyMetadata(null, OnVisualChanged));

    /// <summary>
    /// How many dashes are lit: 0 before the sequence starts, 3 when it is through.
    ///
    /// <para>
    /// Clamped rather than validated. This is drawn from a phase enum that may gain a member, and a
    /// connecting screen that threw because it was handed a four would be a worse outcome than one that
    /// lights every dash it has.
    /// </para>
    /// </summary>
    public int Reached
    {
        get => (int)GetValue(ReachedProperty);
        set => SetValue(ReachedProperty, value);
    }

    public static readonly DependencyProperty ReachedProperty = DependencyProperty.Register(
        nameof(Reached),
        typeof(int),
        typeof(ConnectMark),
        new PropertyMetadata(0, OnVisualChanged));

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ConnectMark)d).Paint();

    private void Paint()
    {
        Wedge.Fill = WedgeFill;
        Wedge.Stroke = WedgeFill;

        DashOne.Fill = Accent;
        DashTwo.Fill = Accent;
        DashThree.Fill = Accent;

        DashOne.Opacity = Reached >= 1 ? 1.0 : Unlit;
        DashTwo.Opacity = Reached >= 2 ? 1.0 : Unlit;
        DashThree.Opacity = Reached >= 3 ? 1.0 : Unlit;
    }
}
