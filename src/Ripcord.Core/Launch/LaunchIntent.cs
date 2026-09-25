namespace Ripcord.Core.Launch;

/// <summary>What the command line asked the app to do on startup.</summary>
public enum LaunchAction
{
    /// <summary>Open normally, at whatever surface the paired-console list calls for.</summary>
    Shell,

    /// <summary>Connect to a named console without stopping at the list.</summary>
    Play,

    /// <summary>Connect to whichever console was played most recently.</summary>
    PlayLast,
}

/// <summary>
/// The parsed command line.
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="Target">
/// For <see cref="LaunchAction.Play"/>, the console's name or id as the caller wrote it. Matching is the
/// caller's job, not this type's — see <see cref="LaunchIntent.Matches"/>.
/// </param>
public readonly record struct LaunchIntent(LaunchAction Action, string? Target)
{
    /// <summary>Open the shell, which is what an argument-free launch and an unparseable one both mean.</summary>
    public static LaunchIntent Shell => new(LaunchAction.Shell, null);

    /// <summary>
    /// Parse the arguments the shell handed us.
    ///
    /// <para>
    /// <b>Why this is in Core and not in the app.</b> It is pure string work, and the alternative is a rule
    /// about how people launch the app that can only be exercised by launching the app — which is how a
    /// feature whose entire purpose is unattended startup ends up untested. Everything here is decided
    /// without a window, a console, or a network.
    /// </para>
    ///
    /// <para>
    /// <b>An unrecognised argument opens the shell rather than failing.</b> These arrive from shortcuts,
    /// launchers and jump lists that the app did not write and cannot fix, often long after whatever wrote
    /// them was configured. A typo in a Steam shortcut should cost a player one extra press, not an error
    /// dialog in front of a game they were trying to start.
    /// </para>
    /// </summary>
    /// <param name="args">The arguments, excluding the executable path.</param>
    public static LaunchIntent Parse(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return Shell;
        }

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i]?.Trim() ?? string.Empty;

            if (Is(arg, "--play-last"))
            {
                return new LaunchIntent(LaunchAction.PlayLast, null);
            }

            if (!Is(arg, "--play"))
            {
                continue;
            }

            // Both spellings, because a shortcut written by hand is as likely to use one as the other and
            // neither is worth being strict about.
            string? value = arg.Contains('=', StringComparison.Ordinal)
                ? arg[(arg.IndexOf('=', StringComparison.Ordinal) + 1)..]
                : i + 1 < args.Count ? args[i + 1] : null;

            value = value?.Trim().Trim('"');

            // "--play" with nothing after it is a shortcut somebody half-wrote. The shell is the honest
            // answer: it shows them every console, which is what they were reaching for.
            return string.IsNullOrWhiteSpace(value)
                ? Shell
                : new LaunchIntent(LaunchAction.Play, value);
        }

        return Shell;
    }

    /// <summary>
    /// Whether a console with this name and id is the one asked for.
    ///
    /// <para>
    /// Name first because that is what a person types into a shortcut, and id as well because a name can be
    /// changed after the shortcut was written. Case-insensitive and trimmed: this is matching something a
    /// human typed, not a protocol field.
    /// </para>
    /// </summary>
    public bool Matches(string? name, string? id)
    {
        if (Action != LaunchAction.Play || Target is not { Length: > 0 } target)
        {
            return false;
        }

        return Same(name, target) || Same(id, target);
    }

    private static bool Same(string? candidate, string target)
        => candidate is { Length: > 0 }
           && string.Equals(candidate.Trim(), target, StringComparison.OrdinalIgnoreCase);

    private static bool Is(string arg, string flag)
    {
        // Leading "/" as well as "-" and "--": a Windows shortcut written by someone used to cmd will use it,
        // and refusing that spelling buys nothing.
        string normalised = arg.TrimStart('/', '-');
        string bare = flag.TrimStart('-');

        return string.Equals(normalised, bare, StringComparison.OrdinalIgnoreCase)
               || normalised.StartsWith(bare + "=", StringComparison.OrdinalIgnoreCase);
    }
}
