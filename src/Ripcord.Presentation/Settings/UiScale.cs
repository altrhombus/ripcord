namespace Ripcord.Presentation.Settings;

/// <summary>
/// How big Ripcord's own text and controls are, as a single multiplier.
///
/// <para>Portable on purpose, and the only part of the scaling feature that is. The arithmetic is a policy
/// decision — what the app-level switch means, how it composes with the operating system's own text size,
/// where the ceiling is — and a policy decision is exactly the kind of thing that should be testable without
/// a window. Applying the number is the front end's problem and lives in <c>AppScale</c>; a macOS or Linux
/// front end would reuse this file and write its own of that one.</para>
/// </summary>
public static class UiScale
{
    /// <summary>
    /// What the app-level switch is worth. A third larger is the smallest step that is unambiguously bigger
    /// across a whole screen rather than looking like a rendering difference.
    /// </summary>
    public const double LargeMultiplier = 1.3;

    /// <summary>
    /// Never below 1.0. An app-level "compact" that undoes an accessibility choice the user made in Windows
    /// is not on offer, so the switch multiplies up or does nothing.
    /// </summary>
    public const double Minimum = 1.0;

    /// <summary>
    /// The ceiling Windows itself uses for text scaling (225%). Past it, layouts stop degrading gracefully
    /// and start losing content, and matching the platform's own limit means a user who has already found
    /// their comfortable size in Windows does not meet a second, different one here.
    /// </summary>
    public const double Maximum = 2.25;

    /// <summary>
    /// <b>Whether this app has to apply the OS text scale itself.</b>
    ///
    /// <para>This is the one value in the feature that rests on a fact about WinUI rather than a decision of
    /// ours, and it is the reason the rest of the file is written to make it a single switch.</para>
    ///
    /// <para>Microsoft's text-scaling documentation says WinUI text controls honour
    /// <c>UISettings.TextScaleFactor</c> with no work from the app, and that <c>IsTextScaleFactorEnabled</c>
    /// defaults to true. <c>docs/history/app-reimagining-plan.md</c> asserts the opposite — that a user at
    /// 150% gets a 100% Ripcord — and then says, in italics, to verify that empirically before building
    /// anything on it. It was never verified.</para>
    ///
    /// <para>The two answers are not a small difference. If WinUI already applies the OS factor and we
    /// multiply by it as well, a user at 150% gets 225% and the feature that was supposed to help them
    /// instead breaks their layout. So the composition is stated here once, as a named constant with its
    /// evidence, rather than spread through the call sites: <b>false means trust the platform.</b></para>
    ///
    /// <para>To settle it: set Windows text size to 150%, launch Ripcord, and look at whether the text is
    /// bigger. If it is not, this becomes <c>true</c> and nothing else changes.</para>
    /// </summary>
    public const bool AppliesOsTextScaleItself = false;

    /// <summary>
    /// The effective multiplier for Ripcord's own sizes.
    /// </summary>
    /// <param name="largeUiScale">The user's app-level switch.</param>
    /// <param name="osTextScaleFactor">
    /// What Windows reports for <c>UISettings.TextScaleFactor</c> (1.0–2.25). Passed in rather than read
    /// here so this stays portable and testable; ignored entirely while
    /// <see cref="AppliesOsTextScaleItself"/> is false, which is what "trust the platform" means.
    /// </param>
    public static double Effective(bool largeUiScale, double osTextScaleFactor)
    {
        double os = AppliesOsTextScaleItself ? Sanitised(osTextScaleFactor) : 1.0;
        double app = largeUiScale ? LargeMultiplier : 1.0;

        return Math.Clamp(os * app, Minimum, Maximum);
    }

    /// <summary>
    /// A size in effective pixels, rounded to a whole one.
    ///
    /// <para>Rounded because a half-pixel lands a one-pixel border across two rows of physical pixels and
    /// renders it grey and soft — most visible on exactly the hairline dividers and focus rings that a
    /// larger UI exists to make easier to see. Never rounds a positive size to zero.</para>
    /// </summary>
    public static double Scaled(double size, double factor)
    {
        if (size <= 0) return size;

        return Math.Max(1, Math.Round(size * factor, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Windows reports 1.0–2.25, but a reading is still an input from outside: a stub, a test double or a
    /// future OS could hand over 0, a NaN, or something absurd, and each of those turns into an invisible
    /// or unusable interface rather than an exception anyone would notice.
    /// </summary>
    private static double Sanitised(double osTextScaleFactor)
        => double.IsFinite(osTextScaleFactor)
            ? Math.Clamp(osTextScaleFactor, Minimum, Maximum)
            : Minimum;
}
