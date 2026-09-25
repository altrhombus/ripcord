using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
}
