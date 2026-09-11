using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Ripcord.Presentation.Settings;
using Windows.UI.ViewManagement;

namespace Ripcord_App.Services;

/// <summary>
/// Applies <see cref="UiScale"/> to this front end's resources, once, at startup.
///
/// <para>Third sibling to <see cref="AppEffects"/> and <c>AppMotion</c>: Windows or the user has said
/// something about how the app should look, and this is the one place that acts on it.</para>
///
/// <para><b>Why this runs once at startup and not when the switch is flipped.</b> WinUI's text ramp is not
/// live-updatable, and finding that out changed the design. The stock TextBlock styles set their size with
/// <c>{StaticResource BodyTextBlockFontSize}</c> and friends — <c>StaticResource</c>, resolved once when the
/// dictionary is parsed and never again. Worse for the obvious fix, <c>XamlControlsResources</c> *defines*
/// those keys itself, so a reference inside it finds them locally and never escalates to
/// <c>Application.Resources</c>: overriding <c>BodyTextBlockFontSize</c> at app level changes nothing at all.
/// <c>docs/history/app-reimagining-plan.md</c> proposed exactly that, on the stated assumption that these were
/// <c>ThemeResource</c> references. They are not, and the plan's mechanism cannot work as written.</para>
///
/// <para>Control-internal text is the opposite case and is handled the easy way: <c>Button</c> and the rest
/// use <c>{ThemeResource ControlContentThemeFontSize}</c>, which does honour an app-level override. So there
/// are two mechanisms here, because the platform has two behaviours, and the file says which is which.</para>
///
/// <para><b>What is deliberately not done:</b> a <c>ScaleTransform</c> on the shell root. Dialogs, flyouts and
/// tooltips render in the XamlRoot's popup root, outside any transform on the content — so every dialog would
/// stay small while the page behind it grew, which is worse than not scaling. It would also blur hairlines
/// and stretch the video <c>SwapChainPanel</c>, whose size is the console's business.</para>
/// </summary>
internal static class AppScale
{
    /// <summary>The factor actually in force, for the diagnostics panel and for tests to read back.</summary>
    public static double Factor { get; private set; } = 1.0;

    /// <summary>
    /// Ripcord's own size tokens, by resource key. Overwriting these works because the *pages* that spend
    /// them are parsed long after this runs — a page's lookup walks up to <c>Application.Resources</c>, whose
    /// own entries are searched before its merged dictionaries, so the scaled value shadows the default in
    /// <c>Styles/Ripcord.Tokens.xaml</c>.
    ///
    /// <para>Corner radii are absent on purpose. A radius is a shape, not a size a person is trying to read
    /// or hit, and scaling it makes large text look like a different application rather than the same one
    /// larger.</para>
    /// </summary>
    private static readonly string[] ScaledDoubles =
    [
        "RipcordSpacingXXS", "RipcordSpacingXS", "RipcordSpacingS", "RipcordSpacingM",
        "RipcordSpacingL", "RipcordSpacingXL", "RipcordSpacingXXL",
        "RipcordIconSizeCaption", "RipcordIconSizeBody", "RipcordIconSizeAction",
        "RipcordIconSizeStanding", "RipcordIconSizeHero",
        "RipcordTouchTarget", "RipcordTouchTargetCompact",
    ];

    /// <summary>The Thickness twins of the spacing scale. XAML cannot do arithmetic, so each step exists in
    /// both forms; both have to move together or padding and gaps disagree at any factor but 1.</summary>
    private static readonly string[] ScaledThicknesses =
    [
        "RipcordInsetXXS", "RipcordInsetXS", "RipcordInsetS", "RipcordInsetM",
        "RipcordInsetL", "RipcordInsetXL", "RipcordInsetXXL",
    ];

    /// <summary>
    /// WinUI's own keys, which stock control templates reach through <c>ThemeResource</c> — so unlike the
    /// text ramp, these can simply be overwritten. This is what keeps the label inside a Button, the text in
    /// a ComboBox and the content of a TextBox growing with everything else.
    /// </summary>
    private static readonly string[] ScaledPlatformDoubles =
    [
        "ControlContentThemeFontSize",
        "TextControlThemeMinHeight",
    ];

    /// <summary>
    /// Apply the scale. Call once, after the settings store exists and <b>before the first window is
    /// constructed</b> — a page's XAML must be parsed after this, or it captures the unscaled values.
    /// </summary>
    public static void Apply(bool largeUiScale)
    {
        double os = ReadOsTextScaleFactor();
        Factor = UiScale.Effective(largeUiScale, os);

        if (Factor == 1.0) return;   // nothing to write, and nothing to get wrong

        ResourceDictionary resources = Application.Current.Resources;

        foreach (string key in ScaledDoubles) ScaleDouble(resources, key);
        foreach (string key in ScaledPlatformDoubles) ScaleDouble(resources, key);
        foreach (string key in ScaledThicknesses) ScaleThickness(resources, key);

        ScaleTextStyles(resources);
    }

    /// <summary>
    /// Give every TextBlock style an explicit, scaled <c>FontSize</c>.
    ///
    /// <para>This is the part that exists because the font-size keys cannot be overridden. Rather than
    /// redeclaring WinUI's seven text styles — which would fork them, and quietly stop tracking whatever the
    /// platform changes about them next — it walks what is already there and adds or replaces one setter.
    /// That also picks up Ripcord's own text styles in the same pass, which matters because they are
    /// <c>BasedOn</c> the stock ones and were parsed with the platform's sizes baked in.</para>
    ///
    /// <para>Mutating a <see cref="Style"/> is only legal before it has been applied to an element, which is
    /// why the ordering requirement on <see cref="Apply"/> is not a preference. If that is ever violated the
    /// framework throws on the sealed style; it is caught per style so a single bad one degrades to
    /// "that text did not scale" rather than a crash on launch, and says so in the debugger.</para>
    /// </summary>
    private static void ScaleTextStyles(ResourceDictionary resources)
    {
        foreach (Style style in TextStyles(resources))
        {
            try
            {
                double? size = DeclaredFontSize(style);
                if (size is null) continue;   // inherits from BasedOn; scaling that one covers this one

                SetFontSize(style, UiScale.Scaled(size.Value, Factor));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppScale: could not scale a text style ({ex.GetType().Name}: {ex.Message}). "
                    + "Most likely Apply ran after a window had already used it.");
            }
        }
    }

    /// <summary>Every TextBlock style reachable from the application's resources, merged dictionaries
    /// included. Enumerating rather than naming keys is the point: Ripcord's own text styles and WinUI's
    /// both need the same treatment, and a list of names would go stale the first time either gains one.</summary>
    private static IEnumerable<Style> TextStyles(ResourceDictionary resources)
    {
        foreach (ResourceDictionary dictionary in WithMerged(resources))
        {
            foreach (object value in dictionary.Values)
            {
                if (value is Style { TargetType: not null } style
                    && style.TargetType == typeof(Microsoft.UI.Xaml.Controls.TextBlock))
                {
                    yield return style;
                }
            }
        }
    }

    private static IEnumerable<ResourceDictionary> WithMerged(ResourceDictionary dictionary)
    {
        yield return dictionary;

        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
        {
            foreach (ResourceDictionary nested in WithMerged(merged)) yield return nested;
        }
    }

    /// <summary>The size this style sets itself, or null if it only inherits one.</summary>
    private static double? DeclaredFontSize(Style style)
    {
        foreach (SetterBase setterBase in style.Setters)
        {
            if (setterBase is Setter { Property: not null } setter
                && setter.Property == Microsoft.UI.Xaml.Controls.TextBlock.FontSizeProperty
                && setter.Value is double size)
            {
                return size;
            }
        }

        return null;
    }

    private static void SetFontSize(Style style, double size)
    {
        for (int i = 0; i < style.Setters.Count; i++)
        {
            if (style.Setters[i] is Setter { Property: not null } setter
                && setter.Property == Microsoft.UI.Xaml.Controls.TextBlock.FontSizeProperty)
            {
                setter.Value = size;
                return;
            }
        }
    }

    private static void ScaleDouble(ResourceDictionary resources, string key)
    {
        if (Lookup(resources, key) is double value) resources[key] = UiScale.Scaled(value, Factor);
    }

    private static void ScaleThickness(ResourceDictionary resources, string key)
    {
        if (Lookup(resources, key) is not Thickness t) return;

        resources[key] = new Thickness(
            UiScale.Scaled(t.Left, Factor), UiScale.Scaled(t.Top, Factor),
            UiScale.Scaled(t.Right, Factor), UiScale.Scaled(t.Bottom, Factor));
    }

    /// <summary>
    /// A key's current value, searching merged dictionaries too.
    ///
    /// <para><c>ResourceDictionary</c>'s indexer looks only at the dictionary's own entries, so asking
    /// <c>Application.Resources</c> for a token declared in <c>Ripcord.Tokens.xaml</c> returns nothing. That
    /// is a silent miss — every token would keep its default and the feature would look like it had simply
    /// not been built — so the search is explicit here rather than left to the indexer.</para>
    /// </summary>
    private static object? Lookup(ResourceDictionary resources, string key)
    {
        foreach (ResourceDictionary dictionary in WithMerged(resources))
        {
            if (dictionary.TryGetValue(key, out object? value)) return value;
        }

        return null;
    }

    /// <summary>
    /// Windows' own text size, 1.0–2.25.
    ///
    /// <para>Read even while <see cref="UiScale.AppliesOsTextScaleItself"/> is false, because it costs
    /// nothing and the diagnostics panel should be able to show what Windows is asking for next to what
    /// Ripcord did about it — which is also how the unverified question this feature rests on gets answered
    /// by someone looking at a screen rather than by argument.</para>
    /// </summary>
    private static double ReadOsTextScaleFactor()
    {
        try
        {
            return new UISettings().TextScaleFactor;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppScale: could not read the OS text scale ({ex.GetType().Name}); assuming 1.0.");
            return 1.0;
        }
    }
}
