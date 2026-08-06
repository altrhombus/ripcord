using System.Text.RegularExpressions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Structural guard on the design system: a page may not declare its own <c>Style</c>.
///
/// <para>
/// <b>Why this is a test.</b> Before the shared dictionaries existed, the same card, chip and pill were
/// re-declared inline across three pages and drifted a little each time. Worse than the drift was a collision
/// nobody noticed: <c>AboutPage</c> and <c>SessionPage</c> both defined <c>RowLabelStyle</c> and
/// <c>RowValueStyle</c> with entirely different definitions, so markup moved between those two pages silently
/// restyled itself. Prose in the dictionary header asked pages not to do this and three pages did it anyway.
/// </para>
///
/// <para>
/// <b>The exemption, and why it is narrow.</b> Item templates and the container style of an items control are
/// allowed. A <c>GridViewItem</c> container style has no shared home by construction — it configures one
/// specific list's items — and a <c>DataTemplate</c> is markup, not a style. Everything else belongs in
/// <c>Styles/Ripcord.*.xaml</c>, where the next surface can find it.
/// </para>
/// </summary>
public class PageStyleTests
{
    /// <summary>
    /// Keys a page may still declare. Container styles for items controls only — see the class note. A key
    /// added here is a claim that no other surface could ever want it, so it should be rare and argued.
    /// </summary>
    private static readonly string[] PermittedKeys =
    [
        "ConsoleCardContainerStyle",
    ];

    [Fact]
    public void NoPageDeclaresItsOwnStyle()
    {
        string appRoot = Path.Combine(RepositoryRoot(), "src", "Ripcord.App");
        Assert.True(Directory.Exists(appRoot), $"Could not locate the app project at {appRoot}.");

        // Only <Style ...> declarations, and only the ones that carry an x:Key — an unkeyed implicit style
        // inside a control template is a different thing and is not what this rule is about.
        var styleWithKey = new Regex(@"<Style\b[^>]*?x:Key=""([^""]+)""", RegexOptions.Singleline);

        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (IsGenerated(file))
            {
                continue;
            }

            // Styles/*.xaml is where styles are supposed to live.
            if (Path.GetDirectoryName(file)?.EndsWith("Styles", StringComparison.Ordinal) == true)
            {
                continue;
            }

            foreach (Match match in styleWithKey.Matches(File.ReadAllText(file)))
            {
                string key = match.Groups[1].Value;
                if (!PermittedKeys.Contains(key))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{key}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A page declared its own Style. Shared styles belong in Styles/Ripcord.*.xaml so the next surface "
            + "can find them, and so two pages cannot give the same key two meanings. Offenders: "
            + string.Join(", ", offenders));
    }

    private static bool IsGenerated(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

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

        throw new InvalidOperationException($"Could not find Ripcord.slnx above {AppContext.BaseDirectory}.");
    }
}
