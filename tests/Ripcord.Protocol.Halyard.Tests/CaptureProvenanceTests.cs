using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Cross-checks every long hex literal in the committed tree against the raw bytes of the dirty-room
/// captures. A literal that appears verbatim in captured traffic was lifted from the wire, whatever the
/// comment above it says.
///
/// <para><b>Why this exists, and why sixteen rounds of <see cref="PublishedTreeSweepTests"/> could not have
/// found it.</b> That sweep asks whether a hex run is <em>declared</em> — allowlisted, pinned, or carrying a
/// comment that says where it came from. Nothing in it can ask whether the declaration is <em>true</em>. On
/// 2026-09-11 a 17-byte fixture in <c>HalyardCtrlMessageTests</c> and <c>ports/ripcord-3ds/tests/session_test.c</c>,
/// labelled "Synthetic … Nothing captured.", turned out to be the payload of a real <c>0x0033</c> control
/// frame, lifted out of <c>cap45.pcapng</c> with its <c>00000011 00330000</c> header still attached. The
/// console encrypts that frame, so the committed bytes were ciphertext; against the plaintext
/// <c>docs/protocol/ps5-session-transport.md</c> publishes for it, they gave up 17 bytes of session
/// keystream. Three audit rounds and a full ports audit cleared it — every one of them by reading the
/// comment.</para>
///
/// <para><b>A false declaration is stronger than no declaration</b>, because an undeclared hex run draws the
/// eye and a declared one is where the reader stops. So provenance cannot stay a claim the tree makes about
/// itself. It is checked here against the only thing that can refute it.</para>
///
/// <para><b>The bound, stated rather than tuned.</b> Sixteen bytes and up, and no entropy or
/// "looks-random" heuristic — those were tried and rejected. A threshold that separates cipher material
/// from framing is a knob whose setting is an argument, and this repository's standing lesson is that a
/// mechanism inherits more generality than its reason required. Instead every literal that legitimately
/// appears in a capture is named in <see cref="Allowed"/> with the sentence explaining why publishing it
/// gives nothing away, and <see cref="EveryAllowedEntry_IsReallyInACapture"/> stops those entries going
/// inert. Fifteen entries is the whole cost.</para>
///
/// <para>Skips when the dirty room is absent, like <see cref="LiveControlVectorTests"/>, so CI stays green
/// and this runs on the machine where fixtures actually get invented.</para>
/// </summary>
public class CaptureProvenanceTests
{
    /// <summary>
    /// Below this, a match is a coincidence rather than evidence: a capture contains every short byte
    /// sequence by construction. At sixteen bytes a chance collision in a corpus this size sits around
    /// 1 in 10^30, so a hit means the bytes were copied. Shorter runs stay the sweep's problem, where a
    /// low bound is the right trade because it reads prose and not traffic.
    /// </summary>
    private const int MinimumBytes = 16;

    /// <summary>
    /// Literals that are in the captures and are allowed to be. Every entry carries the reason it discloses
    /// nothing — that sentence is the guard, not the list. Nothing is exempt here for being *declared*: an
    /// entry earns its place by being a value whose publication gives nothing away, never by having a
    /// comment above it. That distinction is the entire subject of this file.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- Takion's plaintext framing -----------------------------------------------------------------
        // Honestly labelled as captured in TakionTests / TakionDataChunkTests / TakionReliabilityTests, and
        // that label is accurate. These are structural handshake and DATA packets: chunk headers, an
        // ephemeral per-session association tag that dies with the connection, and protobuf bodies carrying
        // a protocol version and a bandwidth probe. No key material, nothing account- or console-derived,
        // and the layout is an interface fact docs/protocol/ documents deliberately. Publishing them is the
        // same act as publishing a sample TCP handshake.
        ["000000000000000000000000000100001400004823000190000064006400004823"] =
            "Takion INIT, client -> console; carries an ephemeral association tag and nothing else",
        ["0000b18ccf000000000000000000010014000048230015000000081ffa01020809"] =
            "Takion DATA, PROTOCOL_VERSION_REQUEST; protobuf body is a version number",
        ["000000482300000000000000000001001400b18ccf000000000008208202020809"] =
            "Takion DATA, PROTOCOL_VERSION_ACK",
        ["0000b18ccf000000000000000000010017000048250008000000080c7206080012020801"] =
            "Takion DATA, BANDWIDTH_PROBE",
        ["0000004823000000000000000003000010000048230001900000000000"] =
            "Takion SACK, client -> console",
        ["0000b18ccf00000000000000000300001000b18cd00001900000000000"] =
            "Takion SACK, console -> client",

        // --- Filler -------------------------------------------------------------------------------------
        // Captures are full of zero runs and all-ones words because padding, cleared buffers and counter
        // blocks look like that. These carry no information in either direction.
        ["00000000000000000000000000000000"] = "sixteen zero bytes - an all-zero AES block",
        ["00000000000000000000000100000000"] = "a single set bit - GCM counter block",
        ["000000000000000000000000ffffffff"] = "counter-block wrap boundary",
        ["ffffffffffffffff0000000000000000"] = "all-ones then all-zeros - a length/boundary probe",
        ["abababababababababababababababab"] =
            "sixteen 0xAB - pairing_set_test.c's FAKE_COMPANION, and what a debug allocator fills "
            + "uninitialised memory with, which is why a heap dump has it",

        // --- Published test vectors ---------------------------------------------------------------------
        // Counting sequences from NIST SP 800-38 / RFC test suites, printed in public standards. They turn
        // up in captured traffic because sequence numbers and payload filler count too.
        ["000102030405060708090a0b0c0d0e0f"] = "NIST/RFC counting vector, published in the standard",
        ["101112131415161718191a1b1c1d1e1f"] = "NIST/RFC counting vector, published in the standard",
        ["202122232425262728292a2b2c2d2e2f"] = "NIST/RFC counting vector, published in the standard",
        ["f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff"] = "NIST/RFC counting vector, published in the standard",
        ["00112233445566778899aabbccddeeff"] = "the canonical nibble-ladder filler, in HARDWARE-PROBES.md",
        ["0102030405060708090a0b0c0d0e0f10"] = "the bytes 1..16 - a counting IV in PacketCryptoHotPathTests",
    };

    /// <summary>Files holding raw wire or memory bytes. Text in the dirty room is this project's own
    /// analysis of its own captures and legitimately quotes values; what must never be copied out is the
    /// traffic itself.</summary>
    private static readonly string[] CaptureExtensions = [".pcapng", ".pcap", ".bin"];

    /// <summary>Source shapes worth reading for a hex literal. Deliberately not
    /// <c>PublishedTreeSweepTests.TextExtensions</c>: this asks a narrower question of a much larger
    /// corpus, and an image or a project file holding a 16-byte hex string is not a thing that happens.</summary>
    private static readonly string[] TextExtensions =
        [".cs", ".c", ".h", ".cpp", ".hpp", ".md", ".json", ".py", ".txt", ".xml", ".yml", ".yaml", ".proto"];

    private static readonly Regex HexLiteral =
        new(@"(?<![0-9a-fA-F])([0-9a-fA-F]{32,})(?![0-9a-fA-F])", RegexOptions.Compiled);

    [SkippableFact]
    public void NoCommittedLiteral_IsInACapture()
    {
        string root = RepoRoot();
        string captures = Path.Combine(root, "docs", "protocol", "captures");
        Skip.IfNot(Directory.Exists(captures), $"dirty room absent ({captures}) - nothing to check against");

        Dictionary<string, List<string>> literals = CommittedLiterals(root);
        Skip.If(literals.Count == 0, "could not read the committed file list (no git?) - nothing to check");

        List<string> offenders = [];
        foreach ((string hex, string where) in Search(captures, literals.Keys))
        {
            offenders.Add($"{hex} ({hex.Length / 2} bytes)"
                + $"\n      committed in : {string.Join(", ", literals[hex].Distinct())}"
                + $"\n      found in     : {where}");
        }

        Assert.True(
            offenders.Count == 0,
            "A committed hex literal appears verbatim in captured traffic, so it was copied off the wire "
            + "rather than invented - whatever the comment above it says. Decide which it is. If it is "
            + "generic protocol structure or a published vector, add it to Allowed with the sentence saying "
            + "why publishing it gives nothing away. If it is session-, console- or account-derived it does "
            + "not belong in the tree at all: replace it with an invented value, record the old one in "
            + "PublishedTreeSweepTests.PurgedValues, and rewrite the history.\n\n  "
            + string.Join("\n\n  ", offenders));
    }

    /// <summary>
    /// An allowlist entry that no longer matches anything is a line of prose pretending to be a control.
    /// This repository has had allowlist entries go inert twice, by three different mechanisms, so the
    /// entries are checked rather than trusted — the same reason the sweep carries
    /// <c>EveryAllowlistEntry_SuppressesSomethingReal</c>.
    /// </summary>
    [SkippableFact]
    public void EveryAllowedEntry_IsReallyInACapture()
    {
        string root = RepoRoot();
        string captures = Path.Combine(root, "docs", "protocol", "captures");
        Skip.IfNot(Directory.Exists(captures), "dirty room absent - cannot confirm the allowlist is live");

        HashSet<string> seen = [.. Search(captures, Allowed.Keys).Select(hit => hit.Hex)];
        string[] inert = [.. Allowed.Keys.Where(k => !seen.Contains(k))];

        Assert.True(
            inert.Length == 0,
            "These entries suppress nothing, so they are documentation rather than exemptions. Either the "
            + "capture they described is gone, or the value was mistyped and the real one is unguarded. "
            + "Confirm which before deleting the line:\n  " + string.Join("\n  ", inert));
    }

    /// <summary>Every hex literal of <see cref="MinimumBytes"/> or more in a committed text file, mapped to
    /// the files holding it. Committed, not on-disk: build trees vendor third-party sources full of curve
    /// constants, and none of that is this project's to answer for.</summary>
    private static Dictionary<string, List<string>> CommittedLiterals(string root)
    {
        string? listing = RunGit(root, "ls-files");
        if (listing is null) return [];

        Dictionary<string, List<string>> byHex = [];
        foreach (string rel in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string relative = rel.Trim();
            if (relative.StartsWith("docs/protocol/captures/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TextExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)) continue;

            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (Match m in HexLiteral.Matches(text))
            {
                string hex = m.Groups[1].Value.ToLowerInvariant();
                if (hex.Length % 2 == 1) hex = hex[..^1];
                if (hex.Length / 2 < MinimumBytes) continue;
                if (Allowed.ContainsKey(hex)) continue;

                byHex.TryAdd(hex, []);
                byHex[hex].Add(relative);
            }
        }

        return byHex;
    }

    /// <summary>Which of <paramref name="wanted"/> appear in the capture bytes, and where.</summary>
    private static IEnumerable<(string Hex, string Where)> Search(string captures, IEnumerable<string> wanted)
    {
        (string Hex, byte[] Bytes)[] needles = [.. wanted.Select(h => (h, Convert.FromHexString(h)))];
        if (needles.Length == 0) yield break;

        foreach (string file in Directory.EnumerateFiles(captures, "*", SearchOption.AllDirectories))
        {
            if (!CaptureExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach ((string hex, byte[] needle) in needles)
            {
                if (bytes.AsSpan().IndexOf(needle) >= 0) yield return (hex, Path.GetFileName(file));
            }
        }
    }

    private static string? RunGit(string root, string arguments)
    {
        try
        {
            using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = root,
                // UTF-8 explicitly. Without this .NET decodes a redirected child's stdout with the
                // console's ANSI code page on Windows, and git emits UTF-8 - so every non-ASCII byte in a
                // commit message arrived mangled. This repository writes em-dashes and ellipses into commit
                // messages constantly, and the truncated-fragment detector keys on the ellipsis, so the
                // message corpus was swept against mojibake on the one platform its author uses. It passed
                // here and failed on every Linux and macOS runner, for the same commit, on the same history:
                // a commit message quoting two pin digests in truncated form sailed past on Windows and was
                // caught on Linux. A guard whose reach depends on the host's code page is not a guard.
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (git is null) return null;
            string output = git.StandardOutput.ReadToEnd();
            git.WaitForExit(120_000);
            return git.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;   // no git on PATH
        }
    }

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
}
