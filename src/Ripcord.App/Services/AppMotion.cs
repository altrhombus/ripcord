using Windows.UI.ViewManagement;

namespace Ripcord_App.Services;

/// <summary>
/// The one place that decides whether the app animates.
///
/// <para>
/// Ripcord had no motion layer at all until the console grid grew one, so there was never anywhere to ask
/// this question. Now there is, and the answer has to be honoured: a user who has turned animation effects
/// off system-wide (Settings → Accessibility → Visual effects) is telling us motion makes the app harder to
/// use, and for some people it is the difference between usable and nauseating.
/// </para>
///
/// <para>
/// XAML's own theme transitions already consult this setting themselves, so they need no gating here. What
/// does need it is anything driven from code — <see cref="Microsoft.UI.Xaml.Media.Animation.ConnectedAnimation"/>
/// above all, which runs regardless. Every such path must also work when it is skipped: motion is the
/// garnish, never the mechanism.
/// </para>
/// </summary>
internal static class AppMotion
{
    /// <summary>Key for the card-to-stream handoff when a console connects.</summary>
    public const string ConnectAnimationKey = "ConnectToConsole";

    // UISettings raises change events, but reading it per animation is cheap and always current, which
    // avoids having to subscribe and unsubscribe from every page that animates.
    private static readonly UISettings Settings = new();

    /// <summary>False when the user has asked Windows for no animation. Check before starting any.</summary>
    public static bool Enabled => Settings.AnimationsEnabled;
}
