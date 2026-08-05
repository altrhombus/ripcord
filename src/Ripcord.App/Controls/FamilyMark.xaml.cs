using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ripcord_App.Controls;

/// <summary>
/// The dash trail from the Ripcord mark, tinted to a console vendor's accent. See the XAML for why it is the
/// trail rather than the whole mark, and brand/README.md for the vendor→colour mapping.
/// </summary>
public sealed partial class FamilyMark : UserControl
{
    public FamilyMark()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The colour to draw the trail in — normally <see cref="Services.ConsoleFamily.AccentBrush"/>.
    ///
    /// <para>
    /// There is deliberately no default. Falling back to a vendor accent would have the app imply a vendor
    /// wherever the mark is used generically (the empty state, the add-a-console tile); falling back to a
    /// theme brush resolved here would freeze at whichever theme was current when the control loaded. Callers
    /// with no family set it to a <c>ThemeResource</c> in markup instead, which tracks the theme properly.
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
        typeof(FamilyMark),
        new PropertyMetadata(null));
}
