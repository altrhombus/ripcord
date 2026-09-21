using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Ripcord.Presentation.Consoles;
using Ripcord_App.Accents;
using Ripcord_App.Services;

namespace Ripcord_App.Converters;

/// <summary>
/// The WinUI translation layer for the portable presentation tokens: role → brush, tone → brush, bool →
/// Visibility, glyph → which icon.
///
/// <para>
/// This is the price of the portable layer holding no UI types, and it is a price worth paying: the decision
/// about what a state <em>means</em> now lives somewhere a test can reach, while the decision about what it
/// <em>looks like</em> stays in the front end where a designer would look for it. Before, both lived in one
/// property and neither could be changed without the other.
/// </para>
///
/// <para>
/// Every brush here is resolved by <c>ThemeResource</c> key rather than as a literal, and resolved defensively:
/// a missing key returns a transparent brush rather than throwing, because a card is far better rendered with
/// one wrong colour than not rendered at all. That is the same lesson the diagnostics overlay's health dot
/// already learned.
/// </para>
/// </summary>
internal static class ThemeBrush
{
    public static Brush Lookup(string key)
        => Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}

/// <summary>Vendor accent as a brush, for marks and strokes.</summary>
public sealed partial class AccentRoleToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => AccentResources.Brush(value is AccentRole role ? role : AccentRole.PlayStation);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Vendor accent as a bare colour, for gradient stops — which take colours, not brushes.</summary>
public sealed partial class AccentRoleToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => AccentResources.Color(value is AccentRole role ? role : AccentRole.PlayStation);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// A status tone as a brush, using the system fill colours rather than literals.
///
/// <para>
/// These specific keys matter: they are the high-contrast-aware system tokens. A previous version of the
/// diagnostics overlay used LimeGreen/Orange literals, which were wrong in light theme and invisible in high
/// contrast — the same trap this converter exists to keep shut for the console cards.
/// </para>
/// </summary>
public sealed partial class StatusToneToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => ThemeBrush.Lookup((value as StatusTone?) switch
        {
            StatusTone.Positive => "SystemFillColorSuccessBrush",
            StatusTone.Caution => "SystemFillColorCautionBrush",
            // Not a Critical fill: an offline console is a normal state, not an error. See StatusTone.Neutral.
            StatusTone.Neutral => "TextFillColorDisabledBrush",
            _ => "TextFillColorTertiaryBrush",
        });

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>true → Visible.</summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}

/// <summary>
/// true → Collapsed. A separate converter rather than a parameter on the one above, because an inverted binding
/// written as a string parameter is easy to typo and fails silently by showing the wrong element.
/// </summary>
public sealed partial class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Collapsed;
}

/// <summary>
/// Visible when the glyph matches the <c>ConverterParameter</c> ("Play" or "Wake"), so the two icons in the
/// action row can each bind the single <see cref="ActionGlyph"/> value.
/// </summary>
public sealed partial class ActionGlyphIsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is ActionGlyph glyph
           && Enum.TryParse(parameter as string, ignoreCase: true, out ActionGlyph wanted)
           && glyph == wanted
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverts a bool, for the bindings whose natural reading is the negative of the state's.
///
/// <para>
/// This exists where a <em>wash</em> converter used to: one accent tint at 0.10 and 0.22 opacity served as
/// both the hover and the focus appearance of a console card. It was deleted rather than retuned, because a
/// tint delta on a gradient that fades out at 90% is not legible on a real screen, high contrast suppressed
/// it to zero, and one shared visual left a pad user unable to tell where focus was. Hover and focus are now
/// different kinds of mark - a wash, and a ring drawn outside the card. Do not reintroduce a single
/// "highlighted" appearance shared by both.
/// </para>
/// </summary>
public sealed partial class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is not true;
}

/// <summary>
/// Dims the primary action for a console that did not answer. Dimmed rather than removed or disabled: the card
/// should still say what it is for, an unreachable console usually just needs switching on, and a probe's
/// silence is not grounds for taking the action away — one lost datagram reads exactly like a console that is
/// off.
/// </summary>
public sealed partial class ReachableOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? 1.0 : 0.5;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
