using System.Text.RegularExpressions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Structural guard on the rule that a crash taught us: the native video-capability queries must be reached
/// through exactly one adapter, which marshals them off the UI thread.
///
/// <para>
/// <b>Why this is a test and not a comment.</b> <c>VideoCapabilities.IsCodecDecodeAvailable</c> is
/// <c>MFStartup</c> plus <c>MFTEnumEx</c>, and calling it on the WinUI UI thread terminates the process with a
/// stowed <c>E_UNEXPECTED</c> — no managed exception, nothing in the crash log, the window simply gone. There
/// were two call sites. The About page wrapped its calls in <c>Task.Run</c> and worked; the settings page did
/// not and crashed on every open. Both were written by people who had read the same code.
/// </para>
///
/// <para>
/// A convention that one of two call sites got wrong is not a convention. So there is now one call site, and
/// this fails the build if a second appears. Same reasoning as
/// <c>BundledInteropConstantsTests.Bundle_CarriesNoLiveVectorMaterial</c>, which is this repo's precedent for
/// enforcing a rule that prose could not hold.
/// </para>
/// </summary>
public class NativeCapabilityAccessTests
{
    /// <summary>The one file allowed to name the native capability class.</summary>
    private const string PermittedFile = "NativeVideoCapabilitiesProbe.cs";

    [Fact]
    public void VideoCapabilities_IsNamedInExactlyOneFile()
    {
        string appRoot = Path.Combine(RepositoryRoot(), "src", "Ripcord.App");
        Assert.True(Directory.Exists(appRoot), $"Could not locate the app project at {appRoot}.");

        // Word-boundary match on the type name, so a comment mentioning "video capabilities" in prose does not
        // trip it but `VideoCapabilities.Anything(` does.
        var pattern = new Regex(@"\bVideoCapabilities\s*\.", RegexOptions.Compiled);

        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Build output, and the generated projections, are not hand-written code.
            if (IsGenerated(file) || Path.GetFileName(file) == PermittedFile)
            {
                continue;
            }

            if (pattern.IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetRelativePath(appRoot, file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Native video-capability queries must go through IVideoCapabilitiesProbe, whose implementation "
            + $"marshals them off the UI thread. Calling them directly kills the process. Offending files: "
            + string.Join(", ", offenders));
    }

    private static bool IsGenerated(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains("Generated Files", StringComparison.Ordinal)
           || path.EndsWith(".g.cs", StringComparison.Ordinal)
           || path.EndsWith(".g.i.cs", StringComparison.Ordinal);

    /// <summary>
    /// Walk up from the test binary until the solution file appears. The test needs to read source rather than
    /// metadata, because the rule is about how code is written, not about what it compiles to.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ripcord.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find Ripcord.slnx above {AppContext.BaseDirectory}.");
    }
}
