using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ripcord_App.Controls;

/// <summary>
/// The play wedge — the slanted zone at a card's trailing edge that carries the primary action. See the XAML
/// for the geometry, its provenance in <c>brand/*.svg</c>, and why an unreachable console keeps a muted wedge
/// rather than losing it.
/// </summary>
public sealed partial class PlayWedge : UserControl
{
    public PlayWedge()
    {
        InitializeComponent();
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
    /// The brush the zone is actually painted with. A read-only projection of the three inputs above rather
    /// than a converter at each binding site, so "which colour is the zone" has one answer in one place.
    /// </summary>
    public Brush? ZoneFill
    {
        get => (Brush?)GetValue(ZoneFillProperty);
        private set => SetValue(ZoneFillProperty, value);
    }

    public static readonly DependencyProperty ZoneFillProperty = DependencyProperty.Register(
        nameof(ZoneFill),
        typeof(Brush),
        typeof(PlayWedge),
        new PropertyMetadata(null));

    private static void OnFillInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PlayWedge)d).UpdateZoneFill();

    private void UpdateZoneFill() => ZoneFill = Muted ? MutedAccent : Accent;
}
