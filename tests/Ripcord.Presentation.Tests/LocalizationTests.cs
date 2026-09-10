using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Structural guard on the portable layer's string catalogue.
///
/// <para>The same reasoning as <see cref="PresentationPortabilityTests"/>: a rule nothing checks decays,
/// and the decay here is silent in both directions. A key left in the resource file after its last use is
/// dead weight a translator still pays for; a key used in code but missing from the file throws
/// <see cref="MissingManifestResourceException"/> at run time, on whichever screen happens to reach it
/// first, in whichever language the user picked. Neither shows up in a build.</para>
///
/// <para><b>What is deliberately not in the catalogue.</b> Three kinds of string stay hard-coded, and the
/// distinction cost a bad automated migration to learn:</para>
/// <list type="bullet">
///   <item>Exception messages for programmer error — they are read in logs and bug reports by people
///     diagnosing a fault, and translating them makes a stack trace less useful, not more.</item>
///   <item>The diagnostics report — a technical dump the user copies into an issue for a maintainer to
///     read. Its audience is the maintainer, so its language follows the maintainer.</item>
///   <item>Protocol and product identifiers — <c>"Ps5"</c>, <c>"PS4"</c>, header names, wire tags. CLAUDE.md
///     already requires these verbatim; a translated wire tag is a bug, not a courtesy.</item>
/// </list>
/// </summary>
public class LocalizationTests
{
    private static readonly Regex KeyUse = new(@"Strings\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    private static DirectoryInfo PresentationRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ripcord.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repository root (no Ripcord.slnx above the test binary)");
        return new DirectoryInfo(Path.Combine(dir!.FullName, "src", "Ripcord.Presentation"));
    }

    private static Dictionary<string, string> ResourceEntries()
    {
        string path = Path.Combine(PresentationRoot().FullName, "Resources", "Strings.resx");
        Assert.True(File.Exists(path), $"the string catalogue is missing: {path}");

        Dictionary<string, string> entries = [];
        foreach (XElement data in XDocument.Load(path).Root!.Elements("data"))
        {
            string? name = data.Attribute("name")?.Value;
            if (name is null) continue;

            Assert.False(entries.ContainsKey(name), $"duplicate resource key: {name}");
            entries[name] = data.Element("value")?.Value ?? string.Empty;
        }

        return entries;
    }

    private static HashSet<string> KeysUsedInSource()
    {
        HashSet<string> used = [];
        foreach (string file in Directory.EnumerateFiles(PresentationRoot().FullName, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            foreach (Match m in KeyUse.Matches(File.ReadAllText(file)))
            {
                used.Add(m.Groups[1].Value);
            }
        }

        return used;
    }

    [Fact]
    public void EveryResourceKey_IsUsed()
    {
        List<string> orphaned = [.. ResourceEntries().Keys.Except(KeysUsedInSource()).Order()];

        Assert.True(
            orphaned.Count == 0,
            "Resource keys nothing uses. Every one of these is a string a translator would be asked to "
            + "translate for no screen at all. Delete the entry, or find the call site that should be using "
            + "it:\n  " + string.Join("\n  ", orphaned));
    }

    [Fact]
    public void EveryUsedKey_Exists()
    {
        Dictionary<string, string> entries = ResourceEntries();

        // Members of the generated accessor that are not resource keys.
        string[] generated = ["ResourceManager", "Culture"];

        List<string> missing = [.. KeysUsedInSource()
            .Where(k => !entries.ContainsKey(k) && !generated.Contains(k))
            .Order()];

        Assert.True(
            missing.Count == 0,
            "Code reads resource keys that the catalogue does not define. This does not fail the build - it "
            + "throws MissingManifestResourceException at run time, on whichever screen reaches it first:\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    public void EveryEntry_HasANonEmptyValueAndATranslatorComment()
    {
        string path = Path.Combine(PresentationRoot().FullName, "Resources", "Strings.resx");
        List<string> bad = [];

        foreach (XElement data in XDocument.Load(path).Root!.Elements("data"))
        {
            string name = data.Attribute("name")?.Value ?? "(unnamed)";
            if (string.IsNullOrWhiteSpace(data.Element("value")?.Value)) bad.Add($"{name}: empty value");

            // A comment is what tells a translator the length limit, the surrounding screen, and which
            // words are product names. Without it they are translating a fragment with no context.
            if (string.IsNullOrWhiteSpace(data.Element("comment")?.Value)) bad.Add($"{name}: no translator comment");
        }

        Assert.True(
            bad.Count == 0,
            "Resource entries that a translator could not act on:\n  " + string.Join("\n  ", bad));
    }

    /// <summary>
    /// The catalogue resolves at run time. Cheap, and it is the only check here that would notice the
    /// resources failing to embed at all — a build-level mistake the other three cannot see, because they
    /// read the <c>.resx</c> from disk rather than the assembly.
    /// </summary>
    [Fact]
    public void TheCatalogue_ResolvesFromTheAssembly()
    {
        Assembly presentation = typeof(IUiDispatcher).Assembly;
        ResourceManager manager = new("Ripcord.Presentation.Resources.Strings", presentation);

        Dictionary<string, string> entries = ResourceEntries();
        Assert.NotEmpty(entries);

        (string key, string expected) = entries.First();
        Assert.Equal(expected, manager.GetString(key, CultureInfo.InvariantCulture));
    }
}
