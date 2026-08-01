namespace Ripcord.Core.Platform;

/// <summary>
/// Where this host keeps per-user configuration, data, and state. A seam because every one of these differs
/// per platform (and because the alternative — hardcoding <c>%LocalAppData%</c> at each call site, or walking
/// up parent directories hunting for a dev-tree file — is what previously made the app unable to find its own
/// configuration once installed rather than run from the repo).
/// </summary>
public interface IPlatformPaths
{
    /// <summary>User configuration the user would expect to survive and be backed up (settings, pairings).</summary>
    string ConfigDirectory { get; }

    /// <summary>Larger or regenerable per-user data (caches, captures).</summary>
    string DataDirectory { get; }

    /// <summary>Logs and traces — losing these costs nothing but diagnostics.</summary>
    string StateDirectory { get; }
}

/// <summary>
/// Conventional per-platform locations, following each platform's own rules rather than imposing Windows'
/// on all of them:
/// <list type="bullet">
///   <item><description>Windows — <c>%LocalAppData%\Ripcord</c> (with <c>\state</c> for logs).</description></item>
///   <item><description>Linux — the XDG base directory spec: <c>$XDG_CONFIG_HOME</c>, <c>$XDG_DATA_HOME</c>,
///   <c>$XDG_STATE_HOME</c>, falling back to <c>~/.config</c>, <c>~/.local/share</c>,
///   <c>~/.local/state</c>.</description></item>
///   <item><description>macOS — <c>~/Library/Application Support</c> and <c>~/Library/Logs</c>.</description></item>
/// </list>
/// Directories are created on construction so callers never have to.
/// </summary>
public sealed class DefaultPlatformPaths : IPlatformPaths
{
    /// <summary>Directory name on Windows/macOS; lowercased for the Linux XDG convention.</summary>
    private const string AppName = "Ripcord";

    public DefaultPlatformPaths(string appName = AppName)
    {
        if (OperatingSystem.IsWindows())
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            ConfigDirectory = Ensure(Path.Combine(root, appName));
            DataDirectory = ConfigDirectory;
            StateDirectory = Ensure(Path.Combine(ConfigDirectory, "state"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            string home = HomeDirectory();
            ConfigDirectory = Ensure(Path.Combine(home, "Library", "Application Support", appName));
            DataDirectory = ConfigDirectory;
            StateDirectory = Ensure(Path.Combine(home, "Library", "Logs", appName));
        }
        else
        {
            // Linux and other unixes: XDG base directories.
            string dir = appName.ToLowerInvariant();
            ConfigDirectory = Ensure(Path.Combine(XdgOr("XDG_CONFIG_HOME", ".config"), dir));
            DataDirectory = Ensure(Path.Combine(XdgOr("XDG_DATA_HOME", Path.Combine(".local", "share")), dir));
            StateDirectory = Ensure(Path.Combine(XdgOr("XDG_STATE_HOME", Path.Combine(".local", "state")), dir));
        }
    }

    public string ConfigDirectory { get; }

    public string DataDirectory { get; }

    public string StateDirectory { get; }

    /// <summary>An absolute path to <paramref name="fileName"/> inside <see cref="ConfigDirectory"/>.</summary>
    public string ConfigFile(string fileName) => Path.Combine(ConfigDirectory, fileName);

    private static string XdgOr(string variable, string relativeFallback)
    {
        string? value = Environment.GetEnvironmentVariable(variable);

        // The spec requires an absolute path; a relative value must be ignored, not silently resolved against
        // the process working directory (which would scatter config wherever the app happened to be launched).
        return !string.IsNullOrEmpty(value) && Path.IsPathRooted(value)
            ? value
            : Path.Combine(HomeDirectory(), relativeFallback);
    }

    private static string HomeDirectory()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Directory.GetCurrentDirectory() : home;
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
