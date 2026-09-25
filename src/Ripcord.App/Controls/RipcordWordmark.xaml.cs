using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ripcord_App.Controls;

/// <summary>
/// The Ripcord wordmark, as outlines. See the XAML for its provenance in
/// <c>brand/ripcord-wordmark.svg</c> and for why it is never shown without the mark.
/// </summary>
public sealed partial class RipcordWordmark : UserControl
{
    public RipcordWordmark()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The word's colour, supplied by the caller in markup so it can be a <c>ThemeResource</c> and follow
    /// the theme — <c>brand/README.md</c> gives graphite on light grounds and white on dark, which is what
    /// the ordinary foreground brush already resolves to.
    ///
    /// <para>
    /// Named <c>Ink</c> rather than <c>Foreground</c>, which a <see cref="UserControl"/> already has: that
    /// one inherits, so a caller who set nothing would get whatever the parent happened to be using and a
    /// caller who meant to be explicit could not tell the difference. No default, for the reason
    /// <see cref="RipcordMark.WedgeFill"/> has none — a brush resolved in code here would freeze at
    /// whichever theme was current when the control loaded.
    /// </para>
    /// </summary>
    public Brush Ink
    {
        get => (Brush)GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(
        nameof(Ink),
        typeof(Brush),
        typeof(RipcordWordmark),
        new PropertyMetadata(null));
}
