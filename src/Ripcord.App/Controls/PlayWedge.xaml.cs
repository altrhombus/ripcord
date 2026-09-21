using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Ripcord_App.Controls;

/// <summary>
/// The play wedge — the slanted zone at a card's trailing edge that carries the primary action. See the XAML
/// for the geometry, its provenance in <c>brand/*.svg</c>, and why an unreachable console keeps a muted wedge
/// rather than losing it.
/// </summary>
public sealed partial class PlayWedge : UserControl
{
    /// <summary>
    /// The slant, as a fraction of the wedge's height. 28/176 is the mark's own proportion.
    ///
    /// <para>
    /// A ratio and not a number of pixels, because the angle is the part that must not change: the diagonal
    /// is the app's signature, and a signature with a different slope on every surface is not one. Holding
    /// the ratio makes the slant identical at 92px beside a grid card and at 184px beside the hero.
    /// </para>
    /// </summary>
    private const double SlantRatio = 28.0 / 176.0;

    /// <summary>The play mark's height, as a fraction of the wedge's. Also taken from the grid card.</summary>
    private const double MarkRatio = 36.0 / 176.0;

    public PlayWedge()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Rebuild the zone for the size we were actually given.
    ///
    /// <para>
    /// Built here rather than declared once in markup because neither Viewbox stretch mode is correct for
    /// this shape: Uniform letterboxes, so a wedge asked to be 184 wide beside a shorter hero card would not
    /// reach the card's edge at all; Fill reaches the edge but changes the slant with the aspect ratio. The
    /// XAML carries the longer version of that argument.
    /// </para>
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = e.NewSize.Width;
        double h = e.NewSize.Height;

        if (w <= 0 || h <= 0)
        {
            return;
        }

        double slant = h * SlantRatio;

        // The plane: the closed quadrilateral.
        var plane = new PathFigure { StartPoint = new Point(slant, 0), IsClosed = true, IsFilled = true };
        plane.Segments.Add(new LineSegment { Point = new Point(w, 0) });
        plane.Segments.Add(new LineSegment { Point = new Point(w, h) });
        plane.Segments.Add(new LineSegment { Point = new Point(0, h) });

        var planeGeometry = new PathGeometry();
        planeGeometry.Figures.Add(plane);
        Zone.Data = planeGeometry;

        var edge = new PathFigure { StartPoint = new Point(slant, 0), IsClosed = false };
        edge.Segments.Add(new LineSegment { Point = new Point(0, h) });

        var edgeGeometry = new PathGeometry();
        edgeGeometry.Figures.Add(edge);
        Rim.Data = edgeGeometry;


        // Centred in the parallel part of the zone rather than in the whole control: the slant eats into the
        // leading edge, so centring on the full width would push the mark visibly off to one side.
        MarkBox.Height = h * MarkRatio;
        MarkBox.Margin = new Thickness(slant, 0, 0, 0);
    }

    /// <summary>
    /// The family accent the zone is drawn in when the console can be reached.
    ///
    /// <para>
    /// No default, for the same reason <see cref="FamilyMark.Accent"/> has none: a fallback vendor colour
    /// would have the app imply a vendor on a generic surface, and a theme brush resolved in code would
    /// freeze at whichever theme happened to be current when the control loaded.
    /// </para>
    /// </summary>
    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(PlayWedge),
        new PropertyMetadata(null, OnFillInputChanged));

    /// <summary>
    /// What the zone falls back to when <see cref="Muted"/> — a neutral, set by the caller in markup so it
    /// tracks the theme.
    /// </summary>
    public Brush MutedAccent
    {
        get => (Brush)GetValue(MutedAccentProperty);
        set => SetValue(MutedAccentProperty, value);
    }

    public static readonly DependencyProperty MutedAccentProperty = DependencyProperty.Register(
        nameof(MutedAccent),
        typeof(Brush),
        typeof(PlayWedge),
        new PropertyMetadata(null, OnFillInputChanged));

    /// <summary>The colour of the play triangle itself — normally the card's fill, so the mark reads as cut out.</summary>
    public Brush MarkFill
    {
        get => (Brush)GetValue(MarkFillProperty);
        set => SetValue(MarkFillProperty, value);
    }

    public static readonly DependencyProperty MarkFillProperty = DependencyProperty.Register(
        nameof(MarkFill),
        typeof(Brush),
        typeof(PlayWedge),
        new PropertyMetadata(null));

    /// <summary>
    /// True when the console did not answer. The wedge goes quiet rather than disappearing.
    ///
    /// <para>
    /// This is the one place the implementation departs from <c>docs/design.md</c>, deliberately and with the
    /// design doc amended to match — the full argument is in the XAML. In short: "cannot be reached" is one
    /// lost UDP datagram away from being wrong, so it may change how the affordance LOOKS and must not remove
    /// it. Muted keeps the doc's reasoning (a console that is off is a normal state, so nothing red and no
    /// error glyph) without rebuilding the dead end in paint.
    /// </para>
    /// </summary>
    public bool Muted
    {
        get => (bool)GetValue(MutedProperty);
        set => SetValue(MutedProperty, value);
    }

    public static readonly DependencyProperty MutedProperty = DependencyProperty.Register(
        nameof(Muted),
        typeof(bool),
        typeof(PlayWedge),
        new PropertyMetadata(false, OnFillInputChanged));

    /// <summary>
    /// The plane's brush — the card's own material, one step lifted. Supplied by the caller in markup so it
    /// tracks the theme, and so this control names no app-level resource of its own.
    /// </summary>
    public Brush FacetFill
    {
        get => (Brush)GetValue(FacetFillProperty);
        set => SetValue(FacetFillProperty, value);
    }

    public static readonly DependencyProperty FacetFillProperty = DependencyProperty.Register(
        nameof(FacetFill),
        typeof(Brush),
        typeof(PlayWedge),
        new PropertyMetadata(null));

    /// <summary>
    /// The brush the rim is stroked with. A read-only projection of the inputs above rather than a converter
    /// at each binding site, so "which colour is the diagonal" has one answer in one place.
    ///
    /// <para>
    /// This drives the <em>rim</em>, not a fill. It filled the whole zone in the first build, and moving it
    /// to the edge is the change that took the wedge from a field of colour to a shape with a coloured edge
    /// — which is what <c>docs/design.md</c> asks for and what survives high contrast.
    /// </para>
    /// </summary>
    public Brush? RimBrush
    {
        get => (Brush?)GetValue(RimBrushProperty);
        private set => SetValue(RimBrushProperty, value);
    }

    public static readonly DependencyProperty RimBrushProperty = DependencyProperty.Register(
        nameof(RimBrush),
        typeof(Brush),
        typeof(PlayWedge),
        new PropertyMetadata(null));

    private static void OnFillInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PlayWedge)d).UpdateRim();

    private void UpdateRim() => RimBrush = Muted ? MutedAccent : Accent;
}
