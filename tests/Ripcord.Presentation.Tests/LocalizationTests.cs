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

    // ---- the XAML half -------------------------------------------------------------------------------
    //
    // These check Ripcord.App's .resw even though this project tests Ripcord.Presentation. No test project
    // targets the app - it is Windows-only and needs the C++ toolchain - and these checks read files from
    // disk rather than touching a WinUI type, so they cost this suite nothing and would otherwise not exist
    // anywhere. The failure they prevent is the one that does not show up in a build: an x:Uid whose entry
    // was renamed resolves to nothing and the control simply renders empty.

    private static DirectoryInfo AppRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ripcord.slnx")))
        {
            dir = dir.Parent;
        }

        return new DirectoryInfo(Path.Combine(dir!.FullName, "src", "Ripcord.App"));
    }

    private static Dictionary<string, string> ReswEntries()
    {
        string path = Path.Combine(AppRoot().FullName, "Strings", "en-US", "Resources.resw");
        Assert.True(File.Exists(path), $"the XAML string catalogue is missing: {path}");

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

    private static readonly Regex UidUse = new(@"x:Uid=""([^""]+)""", RegexOptions.Compiled);

    private static HashSet<string> UidsInMarkup()
    {
        HashSet<string> uids = [];
        foreach (string file in Directory.EnumerateFiles(AppRoot().FullName, "*.xaml", SearchOption.AllDirectories))
        {
            string rel = file.Replace(Path.DirectorySeparatorChar, '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;

            foreach (Match m in UidUse.Matches(File.ReadAllText(file)))
            {
                uids.Add(m.Groups[1].Value);
            }
        }

        return uids;
    }

    [Fact]
    public void EveryUid_HasAResourceEntry()
    {
        // Split on the FIRST dot, not the last: an attached property carries one of its own, so
        // "Page_Thing.AutomationProperties.Name" names the uid "Page_Thing" and not "Page_Thing.AutomationProperties".
        HashSet<string> named = [.. ReswEntries().Keys.Select(k => k[..k.IndexOf('.')])];
        List<string> orphaned = [.. UidsInMarkup().Where(u => !named.Contains(u)).Order()];

        Assert.True(
            orphaned.Count == 0,
            "x:Uid values with no entry in Resources.resw. This does not fail the XAML compiler - the "
            + "control renders with no text at all, which is only visible by running the app and looking "
            + "at the right screen:\n  " + string.Join("\n  ", orphaned));
    }

    [Fact]
    public void EveryReswEntry_IsReferencedByMarkup()
    {
        HashSet<string> uids = UidsInMarkup();
        List<string> unused = [.. ReswEntries().Keys
            .Where(k => !uids.Contains(k[..k.IndexOf('.')]))
            .Order()];

        Assert.True(
            unused.Count == 0,
            "Resource entries no x:Uid references. Either the markup lost its x:Uid, or the entry outlived "
            + "the control it was written for:\n  " + string.Join("\n  ", unused));
    }

    [Fact]
    public void EveryReswEntry_HasAValueAndATranslatorComment()
    {
        string path = Path.Combine(AppRoot().FullName, "Strings", "en-US", "Resources.resw");
        List<string> bad = [];

        foreach (XElement data in XDocument.Load(path).Root!.Elements("data"))
        {
            string name = data.Attribute("name")?.Value ?? "(unnamed)";
            if (string.IsNullOrWhiteSpace(data.Element("value")?.Value)) bad.Add($"{name}: empty value");
            if (string.IsNullOrWhiteSpace(data.Element("comment")?.Value)) bad.Add($"{name}: no translator comment");
        }

        Assert.True(bad.Count == 0, "XAML resource entries a translator could not act on:\n  " + string.Join("\n  ", bad));
    }

    /// <summary>
    /// Types that cannot carry an <c>x:Uid</c>, because MRT applies resources through
    /// <c>FrameworkElement</c> and these do not derive from it.
    ///
    /// <para>This exists because the other guards could not see the failure. Every x:Uid had an entry and
    /// every entry was referenced — the catalogue was perfectly consistent — and the app still died on
    /// launch with "Failed to assign to property 'Microsoft.UI.Xaml.Window.Title'". A <c>Window</c> in
    /// WinUI 3 derives from <c>DependencyObject</c>, so it has no x:Uid support at all, and a migration
    /// that moved its Title into the catalogue produced markup that compiles, indexes into the PRI, and
    /// throws a XamlParseException the first time it is loaded.</para>
    ///
    /// <para>Consistency between markup and catalogue was the wrong question. Whether the element can
    /// receive the resource at all is the one that crashes.</para>
    /// </summary>
    private static readonly string[] CannotCarryUid = ["Window", "Application", "ResourceDictionary"];

    [Fact]
    public void NoUid_OnATypeThatCannotReceiveOne()
    {
        Regex element = new(@"<(" + string.Join("|", CannotCarryUid) + @")(?=[\s>]|$)", RegexOptions.Compiled);
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(AppRoot().FullName, "*.xaml", SearchOption.AllDirectories))
        {
            string rel = file.Replace(Path.DirectorySeparatorChar, '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = element.Match(lines[i]);
                if (!m.Success) continue;

                // Scan this element's attributes: from its tag to the line closing it.
                for (int j = i; j < lines.Length; j++)
                {
                    if (lines[j].Contains("x:Uid=", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{j + 1}  x:Uid on <{m.Groups[1].Value}>");
                    }

                    if (lines[j].TrimEnd().EndsWith(">", StringComparison.Ordinal)) break;
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "x:Uid on a type that cannot receive one. This compiles, indexes into the PRI and passes every "
            + "other check here, then throws XamlParseException the moment the markup loads. Set the "
            + "property in markup or from code-behind instead:\n  "
            + string.Join("\n  ", offenders));
    }
}
