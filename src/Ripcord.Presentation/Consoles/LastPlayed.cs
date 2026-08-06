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
            { TotalMinutes: < 2 } => "Played just now",
            { TotalMinutes: < 60 } => $"Played {(int)ago.TotalMinutes} min ago",
            { TotalHours: < 24 } => $"Played {Plural((int)ago.TotalHours, "hour")} ago",
            { TotalDays: < 7 } => $"Played {Plural((int)ago.TotalDays, "day")} ago",
            _ => $"Played {last.ToLocalTime():d MMM yyyy}",
        };
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
