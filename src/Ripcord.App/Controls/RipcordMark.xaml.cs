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
    // The dashes' own brushes, the three vendors' accents, kept to put back when a contrast theme goes off.
    private readonly Brush[] _trailAccents;

    public RipcordMark()
    {
        InitializeComponent();
        _trailAccents = [TrailOne.Fill, TrailTwo.Fill, TrailThree.Fill];

        Loaded += (_, _) =>
        {
            ApplyContrast();
            AppEffects.Changed += ApplyContrast;
        };
        Unloaded += (_, _) => AppEffects.Changed -= ApplyContrast;
    }

    /// <summary>
    /// In a contrast theme the dashes take the wedge's colour, so the mark is shape alone, as the app icon is
    /// there. The accents are brand colours with no contrast variant, and the mark kept its green, blue and red
    /// under High Contrast (showcase review, H1).
    /// </summary>
    private void ApplyContrast()
    {
        bool contrast = AppEffects.HighContrast;
        TrailOne.Fill = contrast ? WedgeFill : _trailAccents[0];
        TrailTwo.Fill = contrast ? WedgeFill : _trailAccents[1];
        TrailThree.Fill = contrast ? WedgeFill : _trailAccents[2];
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
    /// <summary>
    /// Hides the parts ahead of an <see cref="Assemble"/> that cannot start yet, because the page is not loaded.
    /// Otherwise the finished mark paints first, vanishes when the assembly starts, and comes back: a flicker
    /// (owner, 2026-10-09).
    /// </summary>
    public void HideUntilAssembled()
    {
        if (!AppMotion.Enabled)
        {
            return;
        }

        WedgePart.Opacity = 0;
        TrailOne.Opacity = 0;
        TrailTwo.Opacity = 0;
        TrailThree.Opacity = 0;
    }

    public void Assemble()
    {
        if (!AppMotion.Enabled)
        {
            return;
        }

        TimeSpan step = AppMotion.Duration("RipcordDurationStateChange");
        KeySpline ease = AppMotion.Ease("RipcordEaseEnter");

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

}