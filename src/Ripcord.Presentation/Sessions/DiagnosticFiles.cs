namespace Ripcord.Presentation.Sessions;

/// <summary>
/// The rules for the files a session writes for diagnosis: how their location is shown, and how many are kept.
/// </summary>
public static class DiagnosticFiles
{
    /// <summary>
    /// How many session traces to keep. Every stream writes one, unconditionally, and nothing deleted them: the
    /// machine the visual audit ran on held ninety (2026-10-08). Enough to compare an evening's sessions, which
    /// is what they are read for.
    /// </summary>
    public const int SessionTracesKept = 20;

    /// <summary>
    /// A file's location as it may be shown on screen: under the user's local app data it is written from
    /// <c>%LOCALAPPDATA%</c>, so the Windows user name is not in it.
    ///
    /// <para>
    /// The HUD printed the full path, so every screenshot of the diagnostics somebody posted to a bug report or a
    /// forum carried their Windows user name (visual audit, 2026-10-08). The variable form still pastes straight
    /// into Explorer's address bar, which is all the line is for.
    /// </para>
    /// </summary>
    public static string ForScreen(string path, string localAppData)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (string.IsNullOrEmpty(localAppData))
        {
            return path;
        }

        string root = localAppData.TrimEnd('\\', '/');

        return path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? "%LOCALAPPDATA%" + path[root.Length..]
            : path;
    }

    /// <summary>
    /// Which traces to delete so that <paramref name="keep"/> remain, oldest first. The newest are kept by when
    /// they were written, not by name, so a clock change cannot make a fresh trace look old.
    /// </summary>
    public static IReadOnlyList<string> ToPrune(IEnumerable<(string Path, DateTime Written)> traces, int keep)
    {
        ArgumentNullException.ThrowIfNull(traces);
        ArgumentOutOfRangeException.ThrowIfNegative(keep);

        return [.. traces
            .OrderByDescending(t => t.Written)
            .Skip(keep)
            .Select(t => t.Path)];
    }
}
