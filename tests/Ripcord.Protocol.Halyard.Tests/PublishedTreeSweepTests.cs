using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
/// <para><b>The recurring defect, and the only thing that has ever caught it.</b> Every miss in this file's
/// history has been the same: a scope narrower than the belief held about it — invisible to a reader of the
/// code, obvious the instant the pattern met a candidate input. IPv4-only missed an IPv6. A 24-character
/// floor missed an 8-character registration key. A lowercase class missed an uppercase value. A 16-character
/// floor missed twenty truncated prefixes. A <c>\b</c> anchor missed everything written behind <c>0x</c>.
/// Then, in the very rewrite that catalogued those four: an uncompressed-only IPv6 pattern, a
/// single-space-only byte run, and a prose-only scope that excluded the two <c>.json</c> files which are the
/// only ones in the repository deliberately holding secret-shaped data.</para>
///
/// <para><b>So what is tested here is the belief, not the pattern.</b> <see cref="DetectorContract"/> is a
/// table of inputs this sweep must catch and inputs it must tolerate; every gap ever found is a row in it,
/// and finding another means adding a row rather than reasoning about a regex.
/// <see cref="EveryAllowlistEntry_SuppressesSomethingReal"/> closes the other half — an allowlist entry
/// matching nothing asserts a coverage that does not exist, which has now happened twice by three different
/// mechanisms. Both are CI's problem now rather than an auditor's.</para>
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
        ["00:11:22:33:44:55"] = "synthetic MAC fixture",
        ["77c3673f"] = "a code pointer into the vendor binary, cited by RVA as the naming rules require",
        ["00b18cd0"] = "a Takion TSN sequence number - protocol structure",
        ["4825"] = "a Takion TSN sequence number - protocol structure",
        ["000000215100"] = "a Takion length encoding, given as an observed wire form",
        ["9b4938"] = "an illustrative SourceLink commit suffix in a comment about trimming it",
        ["c200000000000000"] = "the reflected GCM reduction polynomial - a constant from the GCM spec",
        ["01020408102040801b36"] = "the AES Rcon schedule - a constant from FIPS-197",
        ["020004022400551234"] = "a counted-list frame example from the spec's own field tables",
        ["0000000000fe0000"] = "a heartbeat frame - protocol structure, shown to explain a desync",
        ["0000000002010000"] = "a control frame header - protocol structure",
        ["75ac33636696"] = "six independent single bytes, one per wrong-passcode attempt - the passage's point is that each differs",
        ["080852020a00"] = "a Takion DISCONNECT{reason=\"\"} protobuf - identical across sessions, generic structure",
        ["10111314151617"] = "a Takion chunk-type enumeration, given as a byte list in the specs",
        ["0B0101000100"] = "a session-transport frame header - protocol structure",
        ["84D000000000"] = "a session-transport length field - protocol structure",
        ["080110AE0B2001"] = "a Takion control protobuf in a 3DS test comment - generic structure",
        ["080110AE0B180120AE0B"] = "the same protobuf with an extra field - generic structure",
    };

    // A hex run may be introduced by 0x and must not touch other word characters. A \b anchor cannot express
    // this: in "0x4825" there is no boundary between x and 4.
    private const string HexStart = @"(?<![0-9a-zA-Z])(?:0[xX])?";
    private const string HexEnd = @"(?![0-9a-zA-Z])";

    private static readonly Regex TruncatedHex =
        new(HexStart + @"([0-9a-fA-F]{4,})(?:…|\.\.\.)", RegexOptions.Compiled);

    private static readonly Regex SuffixTruncatedHex =
        new(@"(?:…|\.\.\.)([0-9a-fA-F]{4,})" + HexEnd, RegexOptions.Compiled);

    /// <summary>
    /// Twelve, not eight. Eight-character hex collides with RVAs (<c>FUN_101ede80</c>), console error codes
    /// (<c>80108b13</c>) and TSNs, all of which this repository cites deliberately and in volume — the noise
    /// would get the guard switched off, which is worse than the gap. A deliberate trade against "bound low",
    /// stated so it reads as a decision rather than an oversight.
    /// </summary>
    private static readonly Regex LongHex =
        new(HexStart + @"([0-9a-fA-F]{12,})" + HexEnd, RegexOptions.Compiled);

    private static readonly Regex SeparatedMac =
        new(@"(?<![0-9a-zA-Z])[0-9a-fA-F]{2}(?:[:-][0-9a-fA-F]{2}){5}" + HexEnd, RegexOptions.Compiled);

    /// <summary>Six bytes and up, any of space/tab/comma: a key wrapped across lines yields short runs.</summary>
    private static readonly Regex SeparatedByteRun =
        new(@"(?<![0-9a-zA-Z])(?:[0-9a-fA-F]{2}[ \t,]+){5,}[0-9a-fA-F]{2}" + HexEnd, RegexOptions.Compiled);

    private static readonly Regex Ipv4 =
        new(@"(?<![0-9.])((?:\d{1,3}\.){3}\d{1,3})(?![0-9.])", RegexOptions.Compiled);

    /// <summary>
    /// Compressed form included. Every address that would actually identify a network — a residential global
    /// prefix, a link-local, a ULA — is written with <c>::</c> in practice, because that is what every tool
    /// prints. The uncompressed-only version of this pattern was the sixth instance of the same miss.
    /// </summary>
    private static readonly Regex Ipv6 = new(
        @"(?<![0-9a-zA-Z:.])(?:"
        + @"(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}"                                    // eight full groups
        + @"|(?:[0-9a-fA-F]{1,4}:)+:(?:[0-9a-fA-F]{1,4}:)*[0-9a-fA-F]{1,4}"              // a:b::c:d
        + @"|(?:[0-9a-fA-F]{1,4}:)+:"                                                     // a:b::
        + @"|::(?:[0-9a-fA-F]{1,4}:)+[0-9a-fA-F]{1,4}"                                    // ::a:b — two groups,
                                                                                           // so C++ ::name is not an address
        + @")(?![0-9a-zA-Z:.])",
        RegexOptions.Compiled);

    /// <summary>A version attribute is not an address. Decided here rather than left to trip a later widening.</summary>
    private static readonly Regex VersionContext =
        new(@"(?i)version|net\d|\bv\d+\.\d+|TargetFramework|MaxVersionTested|MinVersion", RegexOptions.Compiled);

    private static readonly string[] TextExtensions =
        [".md", ".cs", ".c", ".h", ".json", ".yml", ".yaml", ".xaml", ".py", ".props", ".targets",
         ".csproj", ".vcxproj", ".slnx", ".proto", ".sh", ".ps1", ".editorconfig", ".gitattributes",
         ".appxmanifest", ".manifest"];

    private static readonly string[] NamedFiles = ["NOTICE", "LICENSE", ".gitignore"];

    /// <summary>
    /// The two files that deliberately carry secret-shaped data, pinned by content hash.
    ///
    /// <para>Sweeping their values is useless — they are 1024-character hex tables by design and every
    /// pattern would fire on every one. What is actually wanted is that <em>a change</em> is noticed, because
    /// the real risk is a regeneration quietly sweeping in per-console material. The name-based guard beside
    /// them (<c>BundledInteropConstantsTests</c>) can only see key names, so without this there is no
    /// value-level check at all on the highest-value file in the repository.</para>
    ///
    /// <para>If one fails, do not re-pin reflexively: open the file, confirm against CLAUDE.md's inventory
    /// that nothing per-console has appeared, and only then update the hash.</para>
    /// </summary>
    private static readonly Dictionary<string, string> PinnedDataFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json"] =
            "04bad8c5ba3307f5676632e0e096aae8793b4c628a18b05afe4809f0186771f8",
        ["src/Ripcord.Cloud.Halyard/Data/halyard-oauth-client.json"] =
            "eade485e6c257532339d1d76a3e55b24db03622d14dc37bee19a4aac5bac2fe0",
    };

    /// <summary>
    /// What this sweep must catch, and what it must tolerate. One row per gap ever found. When the next gap
    /// turns up, add the row first — the row is the finding; changing a regex is only how it gets satisfied.
    /// </summary>
    public static TheoryData<string, bool, string> DetectorContract() => new()
    {
        // --- must catch: shapes that have concealed a real value at some point ------------------------
        { "0xdeadbeefcafebabe1234", true, "the 0x prefix defeated the old \\b anchor" },
        { "deadbeefcafebabe1234", true, "plain long hex" },
        { "0x4825abcd…", true, "0x plus truncation" },
        { "4825abcd…", true, "a truncated prefix - the shape of every leak found in round 1" },
        { "…fddb4c", true, "suffix truncation, the mirror shape" },
        { "AA:BB:CC:DD:EE:FF", true, "MAC, uppercase" },
        { "00-11-22-33-44-55", true, "MAC, dash-separated" },
        { "001122334455aa", true, "bare hex below the old 16-character floor" },
        { "<8 bytes, encrypted-frame tail>", true, "space-separated byte run" },
        { "<6 bytes, encrypted-frame tail>", true, "six bytes - a key wrapped across lines yields short runs" },
        { "<7 bytes, encrypted-frame tail>", true, "tab-separated byte run" },
        { "<7 bytes, encrypted-frame tail>", true, "comma-separated byte run" },
        { "172.217.16.14", true, "public IPv4" },
        { "2600:1f18:1a2b:3c4d:5e6f:7a8b:9c0d:1e2f", true, "global IPv6, uncompressed" },
        { "2600:1700:abcd::5", true, "global IPv6, compressed - how a real address is actually written" },
        { "fe80::1c2d:3e4f:5a6b:7c8d", true, "link-local, compressed" },

        // --- must tolerate: things that are not disclosures --------------------------------------------
        { "and so on, continued…", false, "an ordinary prose ellipsis" },
        { "192.0.2.1", false, "RFC 5737 documentation address" },
        { "10.0.0.7", false, "RFC 1918, published as captured by stated policy" },
        { "224.0.0.251", false, "the mDNS multicast group" },
        { "Version=\"1.0.0.0\"", false, "a version quad, not an address - decided rather than discovered" },
        { "fd00:1a2b:3c4d:5e6f:0011:2233:4455:6677", false, "the declared synthetic ULA" },
        { "2001:db8::1", false, "RFC 3849 documentation prefix" },
        { "00000000000000000000", false, "filler, not a value" },
    };

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

    private static bool Excluded(string relative) =>
        relative.Split('/').Any(s =>
            s is "captures" or "bin" or "obj" or "build" or ".git" or ".vs" or ".claude"
              or "node_modules" or "packages" or "Generated Files" or "ARM64" or "x64"
            || s.StartsWith("Ripcord..", StringComparison.Ordinal))
        // This file's own contract table is deliberately leak-shaped.
        || relative.EndsWith("PublishedTreeSweepTests.cs", StringComparison.Ordinal);

    private static IEnumerable<string> CommittedText()
    {
        string root = RepoRoot();
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (Excluded(rel)) continue;
            if (PinnedDataFiles.ContainsKey(rel)) continue;   // covered by hash instead

            if (TextExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                || NamedFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    /// <summary>The dictionary folds case; there is no second clause, and so no doubt that it does.</summary>
    private static bool IsAllowed(string value) => Allowed.ContainsKey(Normalise(value));

    /// <summary>So a dash-separated MAC is looked up as its colon-separated twin.</summary>
    private static string Normalise(string value) =>
        value.Replace('-', ':').Replace(" ", "").Replace("\t", "").Replace(",", "");

    private static bool IsSyntheticFiller(string hex)
    {
        if (hex.Distinct().Count() <= 2) return true;
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
        if (parts.Length != 4 || !parts.All(p => byte.TryParse(p, out _))) return true;
        byte[] o = parts.Select(byte.Parse).ToArray();

        return o[0] switch
        {
            10 or 127 or 0 or 255 => true,
            172 when o[1] >= 16 && o[1] <= 31 => true,
            192 when o[1] == 168 => true,
            192 when o[1] == 0 && o[2] == 2 => true,
            198 when o[1] == 51 && o[2] == 100 => true,
            203 when o[1] == 0 && o[2] == 113 => true,
            224 => true,
            8 when o[1] == 8 && o[2] == 8 && o[3] == 8 => true,
            1 when o[1] == 2 && o[2] == 3 && o[3] == 4 => true,
            _ => false,
        };
    }

    private static bool IsAllowedIpv6(string value)
    {
        string v = value.ToLowerInvariant();
        return v.StartsWith("fd00:", StringComparison.Ordinal)       // the declared synthetic ULA
            || v.StartsWith("2001:db8", StringComparison.Ordinal)    // RFC 3849
            // A bare range name in prose ("a ULA in fc00::/7") names a family, not a host. A full address
            // in the same range still reports, which is the distinction that matters.
            || v is "fc00::" or "fd00::" or "fe80::";
    }

    /// <summary>
    /// The prose part of a line. A comment is prose and the policy reaches it, including a trailing one —
    /// but only the comment, not the code beside it. Returning the whole line put every crypto fixture that
    /// happens to sit next to a trailing comment back into value-scope, which is the noise that gets a guard
    /// deleted.
    /// </summary>
    private static string ProsePortion(string line)
    {
        string s = line.TrimStart();
        if (s.StartsWith("*", StringComparison.Ordinal) || s.StartsWith("#", StringComparison.Ordinal)
            || s.StartsWith("<!--", StringComparison.Ordinal) || s.StartsWith("//", StringComparison.Ordinal)
            || s.StartsWith("/*", StringComparison.Ordinal))
        {
            return line;
        }

        int slash = line.IndexOf("//", StringComparison.Ordinal);
        int block = line.IndexOf("/*", StringComparison.Ordinal);
        int at = slash >= 0 && block >= 0 ? Math.Min(slash, block) : Math.Max(slash, block);
        return at >= 0 ? line[at..] : string.Empty;
    }

    private static string Where(string root, string path, int line)
        => $"{Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')}:{line}";

    /// <summary>Everything the shipped detector reports, so the tests and the contract share one code path.</summary>
    private static List<string> Offenders(bool applyAllowlist = true)
    {
        string root = RepoRoot();
        List<string> found = [];

        foreach (string path in CommittedText())
        {
            string ext = Path.GetExtension(path);
            bool isProse = ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
                           || NamedFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string at = Where(root, path, i + 1);

                foreach (Match m in TruncatedHex.Matches(line))
                {
                    if (applyAllowlist && IsAllowed(m.Groups[1].Value)) continue;
                    found.Add($"{at}|truncated|{m.Groups[1].Value}");
                }

                foreach (Match m in SuffixTruncatedHex.Matches(line))
                {
                    if (applyAllowlist && IsAllowed(m.Groups[1].Value)) continue;
                    found.Add($"{at}|suffix|{m.Groups[1].Value}");
                }

                string prose = isProse ? line : ProsePortion(line);
                if (prose.Length > 0)
                {
                    foreach (Match m in LongHex.Matches(prose))
                    {
                        string hex = m.Groups[1].Value;
                        if (IsSyntheticFiller(hex)) continue;
                        if (applyAllowlist && IsAllowed(hex)) continue;
                        found.Add($"{at}|hex|{hex}");
                    }
                }

                foreach (Match m in SeparatedMac.Matches(line))
                {
                    if (applyAllowlist && IsAllowed(m.Value)) continue;
                    found.Add($"{at}|mac|{m.Value}");
                }

                foreach (Match m in SeparatedByteRun.Matches(prose))
                {
                    string flat = Normalise(m.Value);
                    if (IsSyntheticFiller(flat)) continue;
                    if (applyAllowlist && IsAllowed(flat)) continue;
                    found.Add($"{at}|byte-run|{flat}");
                }

                foreach (Match m in Ipv4.Matches(line))
                {
                    if (IsDocumentationAddress(m.Groups[1].Value)) continue;
                    if (VersionContext.IsMatch(line)) continue;
                    found.Add($"{at}|ipv4|{m.Value}");
                }

                foreach (Match m in Ipv6.Matches(line))
                {
                    if (SeparatedMac.IsMatch(m.Value)) continue;   // a MAC, not an address
                    if (IsAllowedIpv6(m.Value)) continue;
                    found.Add($"{at}|ipv6|{m.Value}");
                }
            }
        }

        return found;
    }

    [Fact]
    public void CommittedText_CarriesNoUnredactedValues()
    {
        List<string> offenders = Offenders();
        Assert.True(
            offenders.Count == 0,
            "Unredacted values in committed text. Replace with a placeholder from the canonical table in "
            + "docs/README.md (Redaction placeholders), or allowlist it with the reason it identifies "
            + "nobody — that sentence is the guard, not the list:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The two data files that hold secret-shaped material by design, checked by content hash. A change to
    /// either is the actual risk, and is the thing no other guard in this repository can see.
    /// </summary>
    [Fact]
    public void PinnedDataFiles_HaveNotChanged()
    {
        string root = RepoRoot();
        List<string> drifted = [];

        foreach ((string rel, string expected) in PinnedDataFiles)
        {
            string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"pinned file missing: {rel}");

            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                drifted.Add($"{rel}\n      expected {expected}\n      actual   {actual}");
            }
        }

        Assert.True(
            drifted.Count == 0,
            "A pinned data file changed. This is the only value-level check on the files the name-based guard "
            + "cannot see into. Open it, confirm against CLAUDE.md's inventory that nothing per-console has "
            + "appeared, and only then re-pin:\n    " + string.Join("\n    ", drifted));
    }

    /// <summary>The belief under test: each row is a gap that was once real, or a value that must not cry wolf.</summary>
    [Theory]
    [MemberData(nameof(DetectorContract))]
    public void Detector_HonoursItsContract(string input, bool shouldMatch, string why)
    {
        bool matched =
            TruncatedHex.IsMatch(input)
            || SuffixTruncatedHex.IsMatch(input)
            || (LongHex.IsMatch(input) && !IsSyntheticFiller(LongHex.Match(input).Groups[1].Value))
            || SeparatedMac.IsMatch(input)
            || SeparatedByteRun.IsMatch(input)
            || (Ipv4.IsMatch(input)
                && !IsDocumentationAddress(Ipv4.Match(input).Groups[1].Value)
                && !VersionContext.IsMatch(input))
            || (Ipv6.IsMatch(input)
                && !SeparatedMac.IsMatch(input)
                && !IsAllowedIpv6(Ipv6.Match(input).Value));

        Assert.True(
            matched == shouldMatch,
            shouldMatch
                ? $"the detector must catch \"{input}\" — {why}"
                : $"the detector must tolerate \"{input}\" — {why}");
    }

    /// <summary>
    /// An allowlist entry matching nothing asserts a coverage that does not exist. That has happened twice —
    /// once because the shipped regex could not reach the text the entry was written for, once because two
    /// new scope rules quietly put the text out of range — and both times an auditor found it rather than CI.
    /// </summary>
    [Fact]
    public void EveryAllowlistEntry_SuppressesSomethingReal()
    {
        HashSet<string> produced = new(StringComparer.OrdinalIgnoreCase);
        foreach (string offender in Offenders(applyAllowlist: false))
        {
            produced.Add(Normalise(offender.Split('|')[^1]));
        }

        List<string> inert = Allowed
            .Where(e => !produced.Any(p => p.Contains(Normalise(e.Key), StringComparison.OrdinalIgnoreCase)))
            .Select(e => $"{e.Key}  (\"{e.Value}\")")
            .ToList();

        Assert.True(
            inert.Count == 0,
            "Allowlist entries that suppress nothing. Each states a reason for tolerating a value the detector "
            + "never produces, which asserts a coverage that does not exist. Either the value is gone (delete "
            + "the entry) or the detector cannot reach it (fix the detector):\n  "
            + string.Join("\n  ", inert));
    }
}
