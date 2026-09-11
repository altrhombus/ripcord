using Ripcord.Presentation.Resources;

namespace Ripcord.Presentation.Consoles;

/// <summary>
/// Phrases when a console was last streamed to, for a caption under its name.
///
/// <para>
/// A separate static with the clock passed in, rather than a property reading <c>DateTimeOffset.UtcNow</c>:
/// every boundary in the ladder below is a place this can be off by one unit, and none of them were testable
/// while "now" was ambient. The thresholds themselves are unchanged from the original.
/// </para>
/// </summary>
public static class LastPlayed
{
    /// <summary>
    /// The caption for <paramref name="lastConnectedUtc"/>, or null when the console has never been played —
    /// which the card renders as nothing at all rather than as "never".
    /// </summary>
    public static string? Describe(DateTimeOffset? lastConnectedUtc, DateTimeOffset now)
    {
        if (lastConnectedUtc is not { } last)
        {
            return null;
        }

        TimeSpan ago = now - last;
        return ago switch
        {
            // A clock that has gone backwards (an NTP correction, or a record written on another machine) must
            // not produce "Played -3 min ago". It reads as just now, which is the least wrong thing to say.
            { TotalMinutes: < 2 } => Strings.LastPlayed_JustNow,
            { TotalMinutes: < 60 } => string.Format(Strings.LastPlayed_MinutesAgo, (int)ago.TotalMinutes),
            { TotalHours: < 24 } => Hours((int)ago.TotalHours),
            { TotalDays: < 7 } => Days((int)ago.TotalDays),
            _ => string.Format(Strings.LastPlayed_OnDate, last.ToLocalTime()),
        };
    }

    // Separate keys rather than appending an "s": pluralisation is not a suffix in most languages, and a
    // helper that assumes it is cannot be translated correctly however carefully the values are written.
    private static string Hours(int n) =>
        n == 1 ? Strings.LastPlayed_OneHourAgo : string.Format(Strings.LastPlayed_HoursAgo, n);

    private static string Days(int n) =>
        n == 1 ? Strings.LastPlayed_OneDayAgo : string.Format(Strings.LastPlayed_DaysAgo, n);
}
