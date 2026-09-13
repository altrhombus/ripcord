using System.Text.RegularExpressions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Structural guards on the design system. Two rules live here, and both are tests rather than prose
/// because prose in a dictionary header has already failed to hold each of them: a page may not declare
/// its own <c>Style</c>, and a page may not hard-code a corner radius the token set already names.
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

    /// <summary>
    /// A page may not hard-code a corner radius at or above the smallest tokenised value.
    ///
    /// <para>
    /// <b>Why the threshold is 4 and not zero.</b> The radius set starts at 4 (tag), so any literal from 4 up
    /// is a value that should have been picked from <c>Ripcord.Tokens.xaml</c> - and eleven of them were,
    /// including a 14 that sat between two tokens and belonged to neither. Below 4 the number is not a pick
    /// from that vocabulary at all: it is a capsule, half the height of the thing it rounds, and rule 3 in the
    /// tokens header explains why filing those into a graded set would make the grading meaningless.
    /// </para>
    ///
    /// <para>
    /// An asymmetric radius ("8,8,0,0") is deliberately not checked. Like an asymmetric Thickness it is a
    /// layout decision made at the site that needs it, and there is no token that could express it.
    /// </para>
    /// </summary>
    [Fact]
    public void NoPageHardCodesARadiusTheTokensAlreadyName()
    {
        string appRoot = Path.Combine(RepositoryRoot(), "src", "Ripcord.App");
        Assert.True(Directory.Exists(appRoot), $"Could not locate the app project at {appRoot}.");

        // Smallest tokenised radius. At or above this, a literal is a token that was not spent.
        const int smallestTokenisedRadius = 4;

        var literalRadius = new Regex(@"CornerRadius=""(\d+(?:\.\d+)?)""");

        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (IsGenerated(file))
            {
                continue;
            }

            // Styles/*.xaml is where the tokens are declared and where spending them is the point.
            if (Path.GetDirectoryName(file)?.EndsWith("Styles", StringComparison.Ordinal) == true)
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = literalRadius.Match(lines[i]);
                if (match.Success
                    && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out double radius)
                    && radius >= smallestTokenisedRadius)
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} (CornerRadius=\"{match.Groups[1].Value}\")");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A page hard-coded a corner radius the token set already names. Spend a token from "
            + "Styles/Ripcord.Tokens.xaml instead - tag 4, card 8, pill 10, panel 12, hero 16 - so the next "
            + "surface picks from the set rather than inventing a seventh value. Offenders: "
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
