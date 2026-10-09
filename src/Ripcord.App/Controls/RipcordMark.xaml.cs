using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Ripcord_App.Services;

namespace Ripcord_App.Controls;

/// <summary>
/// The full Ripcord mark — the play wedge and its three trailing dashes. See the XAML for its provenance in
/// <c>brand/ripcord-mark-ondark.svg</c> and for why this is not <see cref="FamilyMark"/>.
/// </summary>
public sealed partial class RipcordMark : UserControl
{
    public RipcordMark()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The wedge's colour. Supplied by the caller in markup so it can be a <c>ThemeResource</c> and follow
    /// the theme — <c>brand/README.md</c> gives white on dark grounds and near-black on light, which is what
    /// the ordinary foreground brush already resolves to.
    ///
    /// <para>
    /// No default, for the reason <see cref="FamilyMark.Accent"/> has none: a brush resolved in code here
    /// would freeze at whichever theme happened to be current when the control loaded.
    /// </para>
    /// </summary>
    public Brush WedgeFill
    {
        get => (Brush)GetValue(WedgeFillProperty);
        set => SetValue(WedgeFillProperty, value);
    }

    public static readonly DependencyProperty WedgeFillProperty = DependencyProperty.Register(
        nameof(WedgeFill),
        typeof(Brush),
        typeof(RipcordMark),
        new PropertyMetadata(null));

    /// <summary>
    /// Build the mark in front of the player: the wedge first, then the trail streaming out behind it, one dash
    /// at a time. The full mark assembling is design.md's celebration ("the wedge, then the trail"), and Welcome
    /// does it too, so the first time someone sees the mark it arrives the way it means: something launched,
    /// and the trail follows.
    ///
    /// <para>
    /// About 0.6 s from the motion tokens: each part enters in RipcordDurationStateChange on RipcordEaseEnter,
    /// the dashes staggered by half that. With Windows' animation effects off it does nothing, and the mark
    /// is simply there.
    /// </para>
    /// </summary>
    public void Assemble()
    {
        if (!AppMotion.Enabled)
        {
            return;
        }

        TimeSpan step = Token<Duration>("RipcordDurationStateChange", TimeSpan.FromMilliseconds(150)).TimeSpan;
        KeySpline ease = Token<KeySpline>("RipcordEaseEnter", null!) ?? new KeySpline
        {
            ControlPoint1 = new Windows.Foundation.Point(0.1, 0.9),
            ControlPoint2 = new Windows.Foundation.Point(0.2, 1),
        };

        var board = new Storyboard();

        // The wedge comes in from a little behind where it lands; the dashes come out from under it.
        Enter(board, WedgePart, WedgeShift, from: -6, TimeSpan.Zero, step, ease);
        TimeSpan half = TimeSpan.FromTicks(step.Ticks / 2);
        Enter(board, TrailTwo, TrailTwoShift, from: 8, step, step, ease);
        Enter(board, TrailOne, TrailOneShift, from: 8, step + half, step, ease);
        Enter(board, TrailThree, TrailThreeShift, from: 8, step + half + half, step, ease);

        board.Begin();
    }

    private static void Enter(
        Storyboard board, UIElement part, TranslateTransform shift, double from, TimeSpan begin, TimeSpan length, KeySpline ease)
    {
        // Hidden until its turn, so a staggered part is not sitting there finished while it waits.
        part.Opacity = 0;

        board.Children.Add(Animate(part, "Opacity", 0, 1, begin, length, ease));
        board.Children.Add(Animate(shift, "X", from, 0, begin, length, ease));
    }

    private static DoubleAnimationUsingKeyFrames Animate(
        DependencyObject target, string property, double from, double to, TimeSpan begin, TimeSpan length, KeySpline ease)
    {
        var animation = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.Zero, FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = from });
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(begin), Value = from });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(begin + length),
            Value = to,
            KeySpline = new KeySpline { ControlPoint1 = ease.ControlPoint1, ControlPoint2 = ease.ControlPoint2 },
        });

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static T Token<T>(string key, T fallback)
        => Application.Current.Resources.TryGetValue(key, out object? value) && value is T token ? token : fallback;
}