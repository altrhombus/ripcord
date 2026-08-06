using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.UI.ViewManagement;

namespace Ripcord_App.Services;

/// <summary>
/// The one place that decides how much visual effect the app is allowed.
///
/// <para>
/// Sibling to <see cref="AppMotion"/>, and it exists for the same reason: Windows already knows what this user
/// wants, and nothing in Ripcord was asking. Two settings went unread entirely — transparency effects
/// (Settings → Personalisation → Colours) and high contrast — which meant the app looked the same to someone
/// who had explicitly asked Windows for less.
/// </para>
///
/// <para>
/// <b>These are not preferences to weigh against a design.</b> High contrast in particular is not a theme: it
/// is a contract that no colour outside the system palette will appear, because a user may have chosen it to
/// make the screen legible at all. Ripcord's vendor accents are decorative by their own definition — the marks
/// and washes carry no meaning colour is the sole bearer of — so in high contrast they go to zero rather than
/// being adjusted.
/// </para>
///
/// <para>
/// <b>High contrast is read through Win32, not <c>AccessibilitySettings</c>.</b> That WinRT type is the
/// documented answer and it is the wrong one here: subscribing to its <c>HighContrastChanged</c> throws
/// <c>0x80070490</c> in a WinUI 3 desktop app, because the event needs a <c>CoreWindow</c> and a desktop app
/// has none. It took the process down on launch. <c>SystemParametersInfo</c> has no such dependency, and
/// <see cref="UISettings.ColorValuesChanged"/> — which does work here — fires when high contrast is toggled,
/// so the notification still arrives.
/// </para>
/// </summary>
internal static class AppEffects
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HighContrastOn = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }

    // DllImport rather than the newer LibraryImport: the source generator that backs LibraryImport emits
    // unsafe code, which would mean turning AllowUnsafeBlocks on for the whole app project to serve one call.
    // Not a trade worth making for a single well-understood P/Invoke.
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref HighContrastInfo info, uint update);

    // UISettings works in a desktop app; it is only AccessibilitySettings' event that does not. Held statically
    // because these types stop raising events once collected.
    private static readonly UISettings Settings = new();
    private static DispatcherQueue? _dispatcher;

    /// <summary>
    /// Raised when either setting changes, already marshalled onto the UI thread. One event rather than two:
    /// every consumer re-reads whatever it needs, and two events would invite handling one and forgetting the
    /// other — which is how the app came to read neither.
    /// </summary>
    public static event Action? Changed;

    /// <summary>
    /// True when the user permits transparency. When false, every acrylic must resolve to a solid brush — an
    /// acrylic left in place is not merely a preference ignored, it is the specific visual effect someone
    /// turned off, often because it makes text harder to read.
    /// </summary>
    public static bool TransparencyEnabled => Read(() => Settings.AdvancedEffectsEnabled, fallback: true);

    /// <summary>True while a high-contrast theme is active.</summary>
    public static bool HighContrast => Read(IsHighContrastActive, fallback: false);

    /// <summary>
    /// How strongly a decorative vendor accent may show. Zero in high contrast, which is the whole point: the
    /// wash is decoration, and high contrast does not permit decorative colour outside the system palette.
    /// </summary>
    public static double AccentWashOpacity(double requested) => HighContrast ? 0 : requested;

    /// <summary>
    /// Wire up change notification. Called once at startup from the UI thread, so notifications can be
    /// marshalled back to it.
    ///
    /// <para>
    /// Deliberately not a static constructor. A type initialiser that throws poisons the type for the lifetime
    /// of the process — every later access rethrows — and this one subscribes to system events that are
    /// exactly the sort of thing that fails on an unusual host. This version cannot take the app down: the
    /// subscriptions are guarded, and losing them costs a live update, not correctness.
    /// </para>
    /// </summary>
    public static void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        try
        {
            Settings.AdvancedEffectsEnabledChanged += (_, _) => Raise();

            // Also the high-contrast signal: toggling it changes the system colours, so this fires.
            Settings.ColorValuesChanged += (_, _) => Raise();
        }
        catch (Exception ex)
        {
            // The app still reads both settings correctly on every query; only live updates are lost.
            Debug.WriteLine($"[Ripcord] effect-change notifications unavailable: {ex.Message}");
        }
    }

    private static bool IsHighContrastActive()
    {
        var info = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
        return SystemParametersInfo(SpiGetHighContrast, info.Size, ref info, 0)
               && (info.Flags & HighContrastOn) != 0;
    }

    /// <summary>
    /// Answer a system query, falling back rather than throwing. The fallback is always the LESS restrictive
    /// answer, because guessing "high contrast is on" would blank the vendor accents for everyone the moment a
    /// query misbehaved, and that is a worse failure than not honouring a setting we could not read.
    /// </summary>
    private static bool Read(Func<bool> query, bool fallback)
    {
        try
        {
            return query();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Ripcord] effect setting unreadable, assuming {fallback}: {ex.Message}");
            return fallback;
        }
    }

    private static void Raise()
    {
        Action? handler = Changed;
        if (handler is null)
        {
            return;
        }

        if (_dispatcher is { } dispatcher)
        {
            dispatcher.TryEnqueue(() => handler());
            return;
        }

        handler();
    }
}
