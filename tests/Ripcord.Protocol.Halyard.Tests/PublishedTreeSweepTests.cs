using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Sweeps the committed text for material the project's own redaction policy forbids.
///
/// <para><b>Why this exists.</b> The other guards each watch one artefact — two embedded resources and one
/// function's return value. Nothing watched the committed tree, so a console passcode, two device ids and a
/// run of per-registration key fragments reached <c>docs/</c> and stayed.</para>
///
/// <para><b>Bound low, fold case, cover every notation.</b> Five misses so far, all the same shape: a scan
/// matching only IPv4 missed an IPv6; one requiring 24+ characters missed an 8-character registration key;
/// one using a lowercase class missed an uppercase value; one requiring 16+ characters missed twenty
/// truncated prefixes; and the first version of *this* file anchored on <c>\b</c>, which cannot match after
/// the <c>0x</c> that is the dominant hex notation in <c>docs/protocol/</c> — so a full-length secret written
/// <c>0xdeadbeef…</c> passed it silently. Each time the pattern was narrower than the belief about it.</para>
///
/// <para><b>Therefore: never validate an allowlist against a different pattern than the one that ships.</b>
/// The three entries this file first carried were derived from a shell grep with no word-boundary anchor and
/// suppressed nothing, because the shipped regex could not reach the text they were written for. Every entry
/// below is re-derived from what these tests actually report, and
/// <see cref="Sweep_CatchesHexBehindAn0xPrefix"/> exists so that specific defect cannot return unnoticed.</para>
/// </summary>
public class PublishedTreeSweepTests
{
    /// <summary>Values that are legitimately committed, each with the reason it identifies nobody.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["271fa4e2"] = "SHA-256 prefix of the vendor DLL we analysed - provenance evidence",
        ["fddb4"] = "the suffix half of that same vendor DLL hash",
        ["464687b3"] = "the bundled contextKey, published in full in halyard-v1-constants.json anyway",
        ["01000000d08c9ddf"] = "the DPAPI blob magic - a fixed Windows constant, not per-machine",
        ["0000000700410080"] = "the duid's 8-byte constant prefix - identical for every client",
        ["1a2b3c4d5e6f0011"] = "this project's synthetic registration key",
        ["00112233445566778899aabbccddeeff"] = "synthetic filler used in the 3DS probe examples",
        ["001122334455"] = "synthetic host-id used throughout the 3DS tests",
        ["aabbccddeeff"] = "synthetic host-id used throughout the 3DS tests",
        ["00:11:22:33:44:55"] = "synthetic MAC fixture",
        ["77c3673f"] = "a code pointer into the vendor binary, cited by RVA as the naming rules require",
        ["00b18cd0"] = "a Takion TSN sequence number - protocol structure",
        ["4825"] = "a Takion TSN sequence number - protocol structure",
        ["000000215100"] = "a Takion length encoding, given as an observed wire form",
        ["9b4938"] = "an illustrative SourceLink commit suffix in a comment about trimming it",
        ["C200000000000000"] = "the reflected GCM reduction polynomial - a constant from the GCM spec",
        ["01020408102040801b36"] = "the AES Rcon schedule - a constant from FIPS-197",
        ["020004022400551234"] = "a counted-list frame example from the spec's own field tables",
        ["0000000000fe0000"] = "a heartbeat frame - protocol structure, shown to explain a desync",
        ["0000000002010000"] = "a control frame header - protocol structure",
        ["aa:bb:cc:dd:ee:ff"] = "synthetic MAC fixture",
    };

    // A hex run may be introduced by 0x and must not be preceded or followed by other word characters.
    // The old \b anchor could not do this: in "0x4825" there is no boundary between x and 4.
    private const string HexStart = @"(?<![0-9a-zA-Z])(?:0[xX])?";
    private const string HexEnd = @"(?![0-9a-zA-Z])";

    private static readonly Regex TruncatedHex =
        new(HexStart + @"([0-9a-fA-F]{4,})(?:…|\.\.\.)", RegexOptions.Compiled);

    /// <summary>The mirror shape: an ellipsis then the tail of a value.</summary>
    private static readonly Regex SuffixTruncatedHex =
        new(@"(?:…|\.\.\.)([0-9a-fA-F]{4,})" + HexEnd, RegexOptions.Compiled);

    /// <summary>Twelve characters and up: a bare MAC is twelve, below the old sixteen-character floor.</summary>
    private static readonly Regex LongHex =
        new(HexStart + @"([0-9a-fA-F]{12,})" + HexEnd, RegexOptions.Compiled);

    private static readonly Regex SeparatedMac =
        new(@"(?<![0-9a-zA-Z])[0-9a-fA-F]{2}(?:[:-][0-9a-fA-F]{2}){5}" + HexEnd, RegexOptions.Compiled);

    /// <summary>Protocol prose writes keys as separated byte runs; a key hides there in plain sight.</summary>
    private static readonly Regex SeparatedByteRun =
        new(@"(?<![0-9a-zA-Z])(?:[0-9a-fA-F]{2}[ ]){7,}[0-9a-fA-F]{2}" + HexEnd, RegexOptions.Compiled);

    private static readonly Regex Ipv4 =
        new(@"(?<![0-9.])((?:\d{1,3}\.){3}\d{1,3})(?![0-9.])", RegexOptions.Compiled);

    private static readonly Regex Ipv6 =
        new(@"(?<![0-9a-zA-Z:])(?:[0-9a-fA-F]{1,4}:){4,7}[0-9a-fA-F]{1,4}(?![0-9a-zA-Z:])", RegexOptions.Compiled);

    private static readonly string[] TextExtensions =
        [".md", ".cs", ".c", ".h", ".json", ".yml", ".yaml", ".xaml", ".py", ".props", ".targets",
         ".csproj", ".vcxproj", ".slnx", ".proto", ".sh", ".ps1", ".editorconfig", ".gitattributes"];

    /// <summary>Extensionless files that still carry policy-bearing prose.</summary>
    private static readonly string[] NamedFiles = ["NOTICE", "LICENSE", ".gitignore"];

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

    private static bool Excluded(string relative)
    {
        // Path-segment tests, not substring: a dirty room at the repo root produces "captures/..." with no
        // leading separator, which a Contains("/captures/") check would wave through.
        string[] segments = relative.Split('/');
        return segments.Any(s =>
            s is "captures" or "bin" or "obj" or "build" or ".git" or ".vs" or ".claude"
              or "node_modules" or "packages")
            // This file's own regression fixtures are deliberately leak-shaped.
            || relative.EndsWith("PublishedTreeSweepTests.cs", StringComparison.Ordinal);
    }

    private static IEnumerable<string> CommittedText()
    {
        string root = RepoRoot();
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (Excluded(rel)) continue;

            string name = Path.GetFileName(path);
            if (!TextExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                && !NamedFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return path;
        }
    }

    private static bool IsAllowed(string value) =>
        Allowed.ContainsKey(value)
        || Allowed.Keys.Any(k => value.Equals(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>Filler and declared synthetics, matched whole rather than as substrings.</summary>
    private static bool IsSyntheticFiller(string hex)
    {
        if (hex.Distinct().Count() <= 2) return true;   // 0000…, AAAA…, ffff… — filler, not a value
        string[] whole =
        [
            "1234567890123456", "1234567890123456789", "4200000000000000042",
            "3161326233633464", "6162636431323334", "0123456789abcdef",
        ];
        return whole.Contains(hex, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsDocumentationAddress(string ip)
    {
        string[] parts = ip.Split('.');
        if (parts.Length != 4 || !parts.All(p => byte.TryParse(p, out _))) return true;  // not an address
        byte[] o = parts.Select(byte.Parse).ToArray();

        return o[0] switch
        {
            10 => true,                                            // RFC 1918, published as captured
            127 or 0 or 255 => true,                               // loopback / unspecified / broadcast
            172 when o[1] >= 16 && o[1] <= 31 => true,             // RFC 1918
            192 when o[1] == 168 => true,                          // RFC 1918
            192 when o[1] == 0 && o[2] == 2 => true,               // RFC 5737 TEST-NET-1
            198 when o[1] == 51 && o[2] == 100 => true,            // RFC 5737 TEST-NET-2
            203 when o[1] == 0 && o[2] == 113 => true,             // RFC 5737 TEST-NET-3
            224 => true,                                           // multicast (mDNS)
            8 when o[1] == 8 && o[2] == 8 && o[3] == 8 => true,    // well-known public resolver, in prose
            1 when o[1] == 2 && o[2] == 3 && o[3] == 4 => true,    // canonical placeholder
            _ => false,
        };
    }

    /// <summary>A comment in source is prose, and the redaction policy reaches it.</summary>
    private static bool IsCommentLine(string line)
    {
        string s = line.TrimStart();
        return s.StartsWith("//", StringComparison.Ordinal)
            || s.StartsWith("*", StringComparison.Ordinal)
            || s.StartsWith("/*", StringComparison.Ordinal)
            || s.StartsWith("#", StringComparison.Ordinal)
            || s.StartsWith("<!--", StringComparison.Ordinal);
    }

    private static string Where(string root, string path, int line)
        => $"{Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')}:{line}";

    [Fact]
    public void CommittedText_CarriesNoUnredactedValues()
    {
        string root = RepoRoot();
        List<string> offenders = [];

        foreach (string path in CommittedText())
        {
            bool isProse = Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
                           || NamedFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string at = Where(root, path, i + 1);

                foreach (Match m in TruncatedHex.Matches(line))
                {
                    if (!IsAllowed(m.Groups[1].Value)) offenders.Add($"{at}  truncated {m.Value}");
                }

                foreach (Match m in SuffixTruncatedHex.Matches(line))
                {
                    if (!IsAllowed(m.Groups[1].Value)) offenders.Add($"{at}  suffix {m.Value}");
                }

                // Full-length hex is checked in prose only. In a test or a source file a hex literal is a
                // fixture by construction — sweeping those would bury the signal under the crypto vectors
                // and get this guard deleted. A doc comment in code is prose and is checked.
                if (isProse || IsCommentLine(line))
                {
                    foreach (Match m in LongHex.Matches(line))
                    {
                        string hex = m.Groups[1].Value;
                        if (IsAllowed(hex) || IsSyntheticFiller(hex)) continue;
                        offenders.Add($"{at}  hex {hex}");
                    }
                }

                foreach (Match m in SeparatedMac.Matches(line))
                {
                    if (!IsAllowed(m.Value)) offenders.Add($"{at}  mac {m.Value}");
                }

                foreach (Match m in SeparatedByteRun.Matches(line))
                {
                    string flat = m.Value.Replace(" ", "");
                    if (IsAllowed(flat) || IsSyntheticFiller(flat)) continue;
                    offenders.Add($"{at}  byte-run {m.Value}");
                }

                foreach (Match m in Ipv4.Matches(line))
                {
                    if (!IsDocumentationAddress(m.Groups[1].Value)) offenders.Add($"{at}  ipv4 {m.Value}");
                }

                foreach (Match m in Ipv6.Matches(line))
                {
                    string v = m.Value.ToLowerInvariant();
                    if (SeparatedMac.IsMatch(m.Value)) continue;                     // a MAC, not an address
                    if (v.StartsWith("fd00:", StringComparison.Ordinal)) continue;   // the declared synthetic ULA
                    if (v.StartsWith("2001:db8:", StringComparison.Ordinal)) continue; // RFC 3849
                    offenders.Add($"{at}  ipv6 {m.Value}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Unredacted values in committed text. Replace with a placeholder from the canonical table in "
            + "docs/README.md (Redaction placeholders), or allowlist with the "
            + "reason it identifies nobody — that sentence is the guard, not the list:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The regression guard for the defect that made the first version of this file blind: a leading
    /// <c>\b</c> cannot anchor after <c>0x</c>, because <c>x</c> and the first hex digit are both word
    /// characters. Written as an explicit test because the failure mode is silence.
    /// </summary>
    [Fact]
    public void Sweep_CatchesHexBehindAn0xPrefix()
    {
        Assert.Matches(LongHex, "0xdeadbeefcafebabe1234");
        Assert.Matches(LongHex, "deadbeefcafebabe1234");
        Assert.Matches(TruncatedHex, "0x4825abcd…");
        Assert.Matches(TruncatedHex, "4825abcd…");
        Assert.Matches(SuffixTruncatedHex, "…fddb4c");
        Assert.Matches(SeparatedMac, "AA:BB:CC:DD:EE:FF");
        Assert.Matches(SeparatedMac, "00-11-22-33-44-55");
        Assert.Matches(LongHex, "001122334455aa");
        Assert.Matches(SeparatedByteRun, "<8 bytes, encrypted-frame tail>");

        // ...and does not fire on ordinary prose or on a version string.
        Assert.DoesNotMatch(TruncatedHex, "and so on, continued…");
        Assert.False(IsDocumentationAddress("172.217.16.14"));
        Assert.True(IsDocumentationAddress("192.0.2.1"));
        Assert.True(IsDocumentationAddress("10.0.0.7"));
    }
}
