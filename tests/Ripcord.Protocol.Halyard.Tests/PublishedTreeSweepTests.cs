using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Sweeps the published prose for material the project's own redaction policy forbids.
///
/// <para><b>Why this exists.</b> The other guards in this suite each watch one artefact:
/// <c>BundledInteropConstantsTests</c> and <c>HalyardAccountAuthTests</c> guard an embedded resource apiece,
/// and <c>ButtonLabelTests</c> guards one function's return value. Nothing watched the committed tree, so
/// every value that reached <c>docs/</c> in breach of the policy did so unopposed — a console passcode, two
/// device ids, and a run of per-registration key fragments among them.</para>
///
/// <para><b>The lesson encoded in the bound.</b> Those leaks were not random. Every one was a 4-to-8
/// character prefix followed by an ellipsis, because the sweeps that caught their full-length siblings all
/// carried an implicit length floor and could not reach beneath it. The same shape of miss happened three
/// times over: a scan matching only IPv4 missed an IPv6; one requiring 24+ characters missed an 8-character
/// registration key; one using a lowercase character class missed an uppercase value. <b>So bound low, fold
/// case, and prefer a false positive with an allowlist entry to a silent gap.</b></para>
///
/// <para>Allowlisted values carry a reason. If you add one, say why it identifies nobody — that sentence is
/// the actual guard, not the list.</para>
/// </summary>
public class PublishedTreeSweepTests
{
    /// <summary>Truncated-hex fragments that are legitimately published, each with the reason.</summary>
    private static readonly Dictionary<string, string> AllowedFragments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["271fa4e2"] = "SHA-256 of the vendor DLL we analysed - provenance evidence, identifies no user",
        ["464687b3"] = "the bundled contextKey, already published in full in halyard-v1-constants.json",
        ["01000000d08c9ddf"] = "the DPAPI blob magic - a fixed Windows constant, not per-machine",
        ["77c3673f"] = "a code pointer into the vendor binary, cited by RVA as the naming rules require",
        ["00b18cd0"] = "a Takion TSN sequence number - protocol structure",
        ["4825"] = "a Takion TSN sequence number - protocol structure",
        ["0000000700410080"] = "the duid's 8-byte constant prefix - identical for every client",
        ["1a2b3c4d5e6f0011"] = "this project's synthetic registration key, used throughout the docs",
        ["00112233445566778899aabbccddeeff"] = "synthetic filler used in the 3DS probe examples",
    };

    private static readonly Regex TruncatedHex =
        new(@"\b([0-9a-fA-F]{4,})(?:…|\.\.\.)", RegexOptions.Compiled);

    private static readonly Regex LongHex = new(@"\b[0-9a-fA-F]{16,}\b", RegexOptions.Compiled);

    private static readonly Regex MacAddress =
        new(@"\b[0-9a-fA-F]{2}(?:[:-][0-9a-fA-F]{2}){5}\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ripcord.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repository root (no Ripcord.slnx above the test binary)");
        return dir!.FullName;
    }

    /// <summary>The published prose. The dirty room is gitignored and deliberately not swept.</summary>
    private static IEnumerable<string> PublishedProse()
    {
        string root = RepoRoot();
        foreach (string path in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.Contains("/captures/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/build/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/bin/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/obj/", StringComparison.Ordinal)) continue;
            if (rel.StartsWith(".git/", StringComparison.Ordinal)) continue;
            yield return path;
        }
    }

    private static string Where(string root, string path, int lineNumber)
        => $"{Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')}:{lineNumber}";

    [Fact]
    public void PublishedProse_CarriesNoTruncatedSecretFragments()
    {
        string root = RepoRoot();
        List<string> offenders = [];

        foreach (string path in PublishedProse())
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in TruncatedHex.Matches(lines[i]))
                {
                    if (AllowedFragments.ContainsKey(m.Groups[1].Value)) continue;
                    offenders.Add($"{Where(root, path, i + 1)}  {m.Value}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Truncated hex fragments in published prose. Every leak this guard was written for took exactly "
            + "this form — a short prefix plus an ellipsis — so treat each as a redaction that was missed, "
            + "not as noise. Replace with a declared placeholder (<redacted>, <registkey-wire>, <duid>, "
            + "<handshake-key>, <console-ip>), or allowlist it with a reason if it identifies nobody:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void PublishedProse_CarriesNoFullLengthValuesOrMacAddresses()
    {
        string root = RepoRoot();
        List<string> offenders = [];

        foreach (string path in PublishedProse())
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in LongHex.Matches(lines[i]))
                {
                    if (AllowedFragments.ContainsKey(m.Value)) continue;

                    // Filler and synthetic fixtures dominate here; a real value is the one that is neither.
                    if (m.Value.Distinct().Count() <= 2) continue;
                    if (m.Value.Contains("3161326233633464", StringComparison.OrdinalIgnoreCase)) continue;
                    if (m.Value.Contains("1234567890123456", StringComparison.Ordinal)) continue;
                    if (m.Value.Contains("6162636431323334", StringComparison.OrdinalIgnoreCase)) continue;
                    if (m.Value.Contains("4200000000000000042", StringComparison.Ordinal)) continue;

                    offenders.Add($"{Where(root, path, i + 1)}  {m.Value}");
                }

                foreach (Match m in MacAddress.Matches(lines[i]))
                {
                    if (m.Value is "00:11:22:33:44:55" or "aa:bb:cc:dd:ee:ff") continue;
                    offenders.Add($"{Where(root, path, i + 1)}  {m.Value}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Full-length hex runs or MAC addresses in published prose. Check each against the "
            + "generic-versus-personal test before allowlisting:\n  " + string.Join("\n  ", offenders));
    }
}
