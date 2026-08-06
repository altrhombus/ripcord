using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Ripcord.Presentation.Consoles;

namespace Ripcord_App.Accents;

/// <summary>
/// Maps a portable <see cref="AccentRole"/> onto this front end's palette.
///
/// <para>
/// This is the WinUI half of the split that let <see cref="ConsoleFamily"/> become portable. The role travels
/// through the app layer as a plain enum; the colour is looked up here, by key, from
/// <c>Styles/Ripcord.Tokens.xaml</c> — which in turn points at <c>brand/README.md</c>. Resolving by key rather than
/// holding literals means the palette still has exactly one home, and a native macOS or Linux front end
/// supplies its own version of this file rather than inheriting a Windows brush.
/// </para>
///
/// <para>
/// One table, two accessors, because XAML needs both forms and cannot convert between them: a
/// <see cref="Brush"/> for anything that fills or strokes, and a bare <c>Color</c> for gradient stops, which
/// take colours rather than brushes.
/// </para>
/// </summary>
internal static class AccentResources
{
    /// <summary>The brush-resource key for a role.</summary>
    public static string BrushKey(AccentRole role) => role switch
    {
        AccentRole.Xbox => "RipcordXboxAccentBrush",
        AccentRole.Nintendo => "RipcordNintendoAccentBrush",
        _ => "RipcordPlayStationAccentBrush",
    };

    /// <summary>The colour-resource key for a role.</summary>
    public static string ColorKey(AccentRole role) => role switch
    {
        AccentRole.Xbox => "RipcordXboxAccentColor",
        AccentRole.Nintendo => "RipcordNintendoAccentColor",
        _ => "RipcordPlayStationAccentColor",
    };

    /// <summary>The vendor accent as a brush.</summary>
    public static Brush Brush(AccentRole role) => (Brush)Application.Current.Resources[BrushKey(role)];

    /// <summary>The same accent as a bare colour, for gradient stops (which cannot take a brush).</summary>
    public static Windows.UI.Color Color(AccentRole role)
        => (Windows.UI.Color)Application.Current.Resources[ColorKey(role)];
}
