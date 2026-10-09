using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Ripcord_App.Services;

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

        // A breath left running on a page that has gone keeps the storyboard, and the mark, alive.
        Unloaded += (_, _) => Breathe(null);
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

    /// <summary>
    /// The dash for the phase in progress breathes: a slow fade down and back, so a wait that runs to twenty
    /// seconds visibly hasn't stalled. A still mark across a long wake read as a frozen screen (visual audit,
    /// 2026-10-08, C2). Off when the wait is over, and never with Windows' animation effects off, where the dash
    /// is simply lit.
    /// </summary>
    public bool Breathing
    {
        get => (bool)GetValue(BreathingProperty);
        set => SetValue(BreathingProperty, value);
    }

    public static readonly DependencyProperty BreathingProperty = DependencyProperty.Register(
        nameof(Breathing),
        typeof(bool),
        typeof(ConnectMark),
        new PropertyMetadata(false, OnVisualChanged));

    /// <summary>How low the breathing dash fades: well above <see cref="Unlit"/>, so it never reads as unreached.</summary>
    private const double BreathLow = 0.45;

    /// <summary>Half a breath. A full cycle of about 1.2 s is a resting pace, not an alarm.</summary>
    private static readonly TimeSpan HalfBreath = TimeSpan.FromMilliseconds(600);

    private Storyboard? _breath;
    private Rectangle? _breathing;

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ConnectMark)d).Paint();

    private void Paint()
    {
        Wedge.Fill = WedgeFill;

        DashOne.Fill = Accent;
        DashTwo.Fill = Accent;
        DashThree.Fill = Accent;

        DashOne.Opacity = Reached >= 1 ? 1.0 : Unlit;
        DashTwo.Opacity = Reached >= 2 ? 1.0 : Unlit;
        DashThree.Opacity = Reached >= 3 ? 1.0 : Unlit;

        Breathe(Breathing && AppMotion.Enabled ? CurrentDash() : null);
    }

    private Rectangle? CurrentDash() => Math.Clamp(Reached, 0, 3) switch
    {
        1 => DashOne,
        2 => DashTwo,
        3 => DashThree,
        _ => null,
    };

    /// <summary>Start breathing on <paramref name="dash"/>, or stop. A dash already breathing is left mid-breath.</summary>
    private void Breathe(Rectangle? dash)
    {
        if (ReferenceEquals(dash, _breathing))
        {
            return;
        }

        _breath?.Stop();
        _breath = null;
        _breathing = dash;

        if (dash is null)
        {
            return;
        }

        var fade = new DoubleAnimation
        {
            From = 1.0,
            To = BreathLow,
            Duration = new Duration(HalfBreath),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        Storyboard.SetTarget(fade, dash);
        Storyboard.SetTargetProperty(fade, "Opacity");

        _breath = new Storyboard();
        _breath.Children.Add(fade);
        _breath.Begin();
    }
}
