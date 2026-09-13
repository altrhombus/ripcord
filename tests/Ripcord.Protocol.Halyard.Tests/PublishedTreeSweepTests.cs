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
/// <para>Then, one round later, the same shape one level up: the table written to test the belief tested the
/// bare regexes instead of the sweep, so five of its rows certified a catch that does not happen outside
/// prose — and a 64-character capture-derived constant sat in a string literal, unseen, while the table
/// asserted that exact shape was caught.</para>
///
/// <para>And once more, with the narrowing coming from somewhere else entirely: the rule added to catch that
/// constant required the declaration and the value on one line, while <c>.editorconfig</c> caps lines at 120
/// — so any constant larger than the 107-character one that prompted the rule gets wrapped by anyone
/// following the project's own style, straight past a per-line scan. Nothing in the detector was wrong when
/// read on its own; the gap only appeared when it was read against another file. Hence
/// <see cref="CodeHexLiteral"/>, which depends on no syntax at all.</para>
///
/// <para><b>So what is tested here is the belief, not the pattern.</b> <see cref="DetectorContract"/> is a
/// table of inputs this sweep must catch and inputs it must tolerate, each with the
/// <see cref="LineContext"/> it must do so in; every gap ever found is a row in it, and finding another
/// means adding a row rather than reasoning about a regex. Every row runs through
/// <see cref="ScanLine"/> — the one function the file sweep also uses, so the two cannot diverge again.
/// <see cref="EveryAllowlistEntry_SuppressesSomethingReal"/> closes the other half — an allowlist entry
/// matching nothing asserts a coverage that does not exist, which has now happened twice by three different
/// mechanisms. What no pattern can reach is pinned by value instead
/// (<see cref="PinnedDataFiles"/>, <see cref="PinnedConstants"/>). All of it is CI's problem now rather than
/// an auditor's.</para>
///
/// <para><b>And the corpus can be narrower than the artifact.</b> Four rounds of this were patterns too
/// narrow for the belief about them; the fifth was different in kind. Every check here read files, while a
/// clone also carries commit messages, author identity and timestamps — none of them files, all of them
/// published. The proof was a redaction that held in the source comment and not in the message of the commit
/// that made it. <see cref="CommitMessages_CarryNoUnredactedValues"/> closes the message corpus. Author
/// identity and the committer timezone remain outside every guard here, by nature: see
/// <c>docs/README.md</c>, which records what that discloses and why it is accepted rather than rewritten.</para>
/// </summary>
public class PublishedTreeSweepTests
{
    /// <summary>Values that are legitimately committed, each with the reason it identifies nobody.</summary>
    // The allowlists are data that must be leak-shaped to work, same as the contract table below. The
    // exemption covers the tables and nothing else; the prose around them is swept like any other prose.
    // <sweep:fixtures>
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
        ["0000b18ccf000000000000000000010014000048230015000000081ffa01020809"] =
            "a captured Takion DATA chunk, the reassembly parser's ground truth - header structure, tag and TSN, no key material",
        ["0000004823000000000000000003000010000048230001900000000000"] =
            "a captured Takion SACK, same - the retransmit tests parse it and assert the gap blocks",
        ["69c4e0d86a7b0430d8cdb78070b4c55a"] =
            "the FIPS-197 AES-128 ECB known-answer ciphertext - a published NIST vector, in the 3DS test runner",
        ["ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"] =
            "the base64 alphabet itself, in the 3DS port's encoder - RFC 4648 table 1, not a value",

    };

    /// <summary>
    /// Allowed in <b>commit messages</b> only. Separate from <see cref="Allowed"/> because reachability has
    /// to be checked in the corpus an entry claims, and this corpus can be legitimately absent: a source
    /// archive has no history, and these would otherwise read as dead entries and turn the suite red for
    /// anyone who unpacked a tarball. Suppression is identical either way —
    /// <see cref="IsAllowed"/> consults both.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedInMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0000000000fe"] =
            "the heartbeat frame from that same message, six bytes of it - protocol structure, and the "
            + "longer form is already allowlisted above for the docs explaining the desync",
        ["deadbeefcafebabe1234"] =
            "this file's own synthetic contract value, quoted in two later commit messages explaining the "
            + "gap it exposed - made up, and the whole point of it is that it looks like a secret",
        ["2001:db8a::"] =
            "the RFC 3849 near-miss from the contract table, quoted in the commit message that added it - "
            + "documentation-adjacent by construction and routed nowhere",
    };
    // </sweep:fixtures>

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

    /// <summary>
    /// Any hex string literal of sixteen bytes or more, wherever it sits on the line. Applied to
    /// <b>product code only</b> (<see cref="IsProductCode"/>), where it is the entire rule and there is
    /// nothing to bypass: no dependence on <c>const</c>, on <c>readonly</c>, on the literal sharing a line
    /// with its declaration, or on the quoting form. <c>@"…"</c>, <c>"""…"""</c>, a value wrapped onto its
    /// own line, an expression-bodied member and <c>Convert.FromHexString("…")</c> all read alike to it,
    /// and either quote character serves — the one <c>.py</c> file in this corpus is the OAuth extractor,
    /// Python single-quotes by convention, and a double-quote-only rule could not see into it.
    ///
    /// <para>This replaced a rule that required a declaration on one line. That rule caught a fourth
    /// constant written exactly the way the third was, and six other forms walked past it — the worst being
    /// the wrapped declaration, which <c>.editorconfig</c>'s 120-column limit makes the <em>expected</em>
    /// form for any constant larger than the 107-character one that motivated the guard. A guard whose
    /// reach depends on the secret being short enough to fit a style rule is not a guard.</para>
    ///
    /// <para><b>It is absolute because it costs nothing.</b> Product code holds exactly one 32+ character
    /// hex literal today — <c>ClientTypeHex</c>, pinned below — so every future one is a deliberate act
    /// that has to be argued into the allowlist. Test code is a different corpus and a different question:
    /// 83 sites, 61 distinct values, essentially all published NIST vectors and captured protocol frames.
    /// Inverting over those would add 61 entries reading "a crypto test vector" and bury the 28 whose
    /// reasons <em>are</em> the guard. Tests keep the narrower declaration rule
    /// (<see cref="DeclaredHexConstantInTests"/>), and the forms it misses are contract rows rather than
    /// something for the next audit to rediscover.</para>
    /// </summary>
    private static readonly Regex CodeHexLiteral =
        new(@"[""']([0-9a-fA-F]{32,})[""']", RegexOptions.Compiled);

    /// <summary>
    /// Base64 of sixteen bytes or more in shipping code. With hex absolute, this is the plausible
    /// remaining way to ship a secret: a 32-byte key is 44 base64 characters, and <c>&lt;base64&gt;</c>
    /// is already a placeholder in this project's canonical table with nothing enforcing it. Raised in
    /// round 1 and the one item from that round never taken up.
    ///
    /// <para>Both alphabets: standard and base64url (<c>-_</c>, RFC 4648 §5). base64url is the convention
    /// throughout OAuth and JWT, which is exactly this project's cloud layer — the bundled credential, PSN
    /// access and refresh tokens — so it is the form a secret from there would arrive in. Widening the class
    /// costs nothing measurable: at the 40-character floor both alphabets surface the same single site.</para>
    ///
    /// <para><b>The 40-character floor is 30 bytes, and that is a decision, not a match for the hex rule's
    /// 16.</b> The gap between them covers AES-128 — a 16-byte key is 32 hex characters and caught, 24
    /// base64 characters and missed — which is this protocol's core primitive. It is left open because
    /// closing it costs more than it buys: measured over the 373 product-code files, a 40-character floor
    /// surfaces 1 distinct value, 32 characters surfaces 12, and 24 characters surfaces 84 across 127 sites
    /// — almost entirely identifier strings, WinUI brush names, XAML keys, type names and HTTP header
    /// names. That is the noise this project has refused elsewhere, and it would bury the entries whose
    /// reasons are the guard. A mitigation worth having would key on entropy or on proximity to a crypto
    /// identifier, not on length.</para>
    ///
    /// <para>Costs one allowlist entry: the only 40+ character base64 literal in shipping code is the
    /// alphabet itself, in the 3DS encoder. Pure hex is skipped at the call site rather than excluded in
    /// the pattern, because hex is a subset of the base64 alphabet and the hex rule has already reported
    /// it — excluding it here would only duplicate the finding.</para>
    /// </summary>
    private static readonly Regex CodeBase64Literal =
        new(@"[""']([A-Za-z0-9+/_-]{40,}={0,2})[""']", RegexOptions.Compiled);

    private static readonly Regex PureHex = new("^[0-9a-fA-F]+$", RegexOptions.Compiled);

    /// <summary>Matches when the text immediately before a literal is a XAML identifier attribute.</summary>
    private static readonly Regex XamlIdentifierAttribute =
        new(@"x:(Uid|Name|Key)=$", RegexOptions.Compiled);

    /// <summary>
    /// The declaration-shaped rule, kept for test code only. It reports four sites there: three allowlisted
    /// with their reasons and one suppressed as filler. (An earlier version of this comment said five sites
    /// and four allowlisted; it counted <c>ClientTypeHex</c>, which is product code and pinned, and it
    /// counted the filler one as allowlisted. A comment justifying a security scope in this file of all
    /// files should be countable, so it is now the measured figure.) What this rule misses in a test file is
    /// an accepted trade — the risk the mechanism exists for is a capture-derived constant <em>shipped in
    /// the product</em>, and that corpus is covered without exception above.
    /// </summary>
    private static readonly Regex DeclaredHexConstantInTests =
        new(@"\b(?:const|readonly)\b[^""=]*=\s*""([0-9a-fA-F]{32,})""", RegexOptions.Compiled);

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
        + @"|(?:[0-9a-fA-F]{1,4}:)+:(?:[0-9a-fA-F]{1,4}:)*[0-9a-fA-F]{1,4}"              // groups, gap, groups
        + @"|(?:[0-9a-fA-F]{1,4}:)+:"                                                     // groups, then a gap
        + @"|::(?:[0-9a-fA-F]{1,4}:)+[0-9a-fA-F]{1,4}"                                    // gap, then two or more
                                                                                           // groups, so a C++ scope
                                                                                           // resolution is not an address
        + @")(?![0-9a-zA-Z:.])",
        RegexOptions.Compiled);

    /// <summary>
    /// Dotted decimals that are not addresses. A version attribute was the first kind; a specification
    /// clause reference is the second, and it arrived with ripcord-ps3 - H.264 is cited by clause
    /// throughout, and exactly-four-part numbers like <c>sec 7.4.1.1</c> are indistinguishable from an
    /// IPv4 address to a pattern. Three- and five-part references never collided - <c>9.1.1</c> is too
    /// short for the quad and <c>7.4.1.2.4</c> too long - which is why this went unnoticed until a file
    /// full of four-part ones existed.
    ///
    /// <para>The clause alternative is deliberately tight: the citation word must be immediately
    /// followed by the dotted number, so "in section 3" beside a real address suppresses nothing, which
    /// a bare word match would not have managed. The residual cost is the one this guard always had -
    /// it is tested per line, so a genuine address sharing a line with a clause citation is missed.
    /// Both properties are contract rows.</para>
    /// </summary>
    private static readonly Regex NotAnAddressContext =
        new(@"(?i)version|net\d|\bv\d+\.\d+|TargetFramework|MaxVersionTested|MinVersion"
            + @"|\b(?:sec|section|clause|annex)\.?\s*\d+(?:\.\d+)+", RegexOptions.Compiled);

    /// <summary>
    /// <c>.cpp</c>, <c>.hpp</c>, <c>.idl</c> and <c>.def</c> were missing until the tenth review, so the
    /// entire C++/WinRT interop layer — fourteen committed files under <c>src/</c>, which is the corpus
    /// this file makes <em>absolute</em> for hex literals on the grounds that it is "the corpus a user
    /// runs" — was outside every sweep, tree and historical alike. Clean when finally read, but unread for
    /// eight rounds while the coverage of everything else was argued in detail.
    ///
    /// <para>The lesson is cheaper than the miss: the question to ask of a corpus is not "does the list
    /// look complete" but "which committed files does it not open". That is one command —
    /// <c>git ls-files</c> minus this list — and it is what <see cref="EveryCommittedTextFile_IsSwept"/>
    /// now asks on every run.</para>
    /// </summary>
    private static readonly string[] TextExtensions =
        [".md", ".cs", ".c", ".h", ".cpp", ".hpp", ".idl", ".def", ".json", ".yml", ".yaml", ".xaml",
         ".py", ".props", ".targets", ".csproj", ".vcxproj", ".slnx", ".proto", ".sh", ".ps1",
         ".editorconfig", ".gitattributes", ".appxmanifest", ".manifest", ".svg", ".resx", ".resw"];

    private static readonly string[] NamedFiles = ["NOTICE", "LICENSE", ".gitignore", "Makefile"];

    /// <summary>
    /// Extensions that are genuinely not text, so their absence from the corpus is not a gap.
    ///
    /// <para><c>.svg</c> was on this list and should never have been: all five committed SVGs are UTF-8
    /// text carrying human prose comments, and SVG can hold <c>&lt;metadata&gt;</c>, <c>&lt;desc&gt;</c>,
    /// <c>data:</c> URIs and editor-inserted author strings — exactly the class widening the corpus existed
    /// for. It is in <see cref="TextExtensions"/> now, and
    /// <see cref="EveryCommittedTextFile_IsSwept"/> no longer takes this list's word for it.</para>
    ///
    /// <para><c>.pcapng</c> and <c>.bin</c> were also here, which gave a committed capture a route
    /// <em>past</em> the corpus guard as "declared binary". Capture types are on
    /// <see cref="NeverCommitted"/> instead, where the answer is not "skip it" but "fail". <c>.gz</c> and
    /// <c>.zip</c> came off for the same reason one round later — a <c>.pcapng.gz</c> resolved to
    /// <c>.gz</c> and was skipped as declared binary. An opaque container is not a thing this guard can
    /// vouch for, and nothing here legitimately is one, so it now fails as unclassified.</para>
    /// </summary>
    private static readonly string[] BinaryExtensions =
        [".png", ".ico", ".jpg", ".jpeg", ".gif", ".bmp", ".ttf", ".otf", ".pfx", ".dll", ".pri",
         ".exe", ".lib", ".pdb"];

    /// <summary>
    /// File types that must never be committed, whatever directory they are in.
    ///
    /// <para>The dirty-room rule was enforced for eleven rounds by one <c>.gitignore</c> line naming one
    /// directory, so a capture saved anywhere else was not ignored at all. These types definitionally hold
    /// raw session material — <c>docs/protocol-research-log.md</c> enumerates it: OAuth access and refresh
    /// tokens, authorization codes, WebAuthn material, account identifiers, date of birth, device ids,
    /// console names, candidate IP addresses. The whole redaction architecture rests on none of them being
    /// committed, and until now that rested on remembering where to save.</para>
    ///
    /// <para>Deliberately not a skip and not an allowlist entry. There is no reason to commit one of these
    /// and no benign form of doing so, which is the only case in this file that warrants an outright
    /// refusal rather than a scope decision.</para>
    /// </summary>
    /// <summary>
    /// Every extension in a filename, not just the last. <c>session5-wireshark.pcapng.gz</c> yields both
    /// <c>.pcapng</c> and <c>.gz</c>.
    ///
    /// <para>Written because <see cref="NeverCommitted"/> was matched against
    /// <c>Path.GetExtension</c> alone, and five of the ten capture filenames in this project's own
    /// research log end <c>.pcapng.gz</c> — so the rule was defeated by the form the documented workflow
    /// actually produces. The rule was written against the file <em>type</em> and matched against the
    /// file <em>name</em>, which is the same gap between belief and mechanism every round of this has
    /// turned on, arriving at the shallowest layer there is.</para>
    /// </summary>
    private static IEnumerable<string> Extensions(string path)
    {
        string name = Path.GetFileName(path);
        for (int i = name.IndexOf('.'); i >= 0 && i < name.Length - 1; i = name.IndexOf('.', i + 1))
        {
            int next = name.IndexOf('.', i + 1);
            yield return next < 0 ? name[i..] : name[i..next];
        }
    }

    private static readonly string[] NeverCommitted =
        [".pcap", ".pcapng", ".cap", ".saz", ".har", ".etl", ".utrace", ".bin"];

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
    ///
    /// <para>Hashed with line endings normalised to LF, which is how git stores them. The first version of
    /// this hashed the bytes on disk and so pinned the <em>checkout</em> rather than the content — green on
    /// Windows, red on every Linux and macOS runner, for weeks, because nobody had looked at a CI result.</para>
    /// </summary>
    private static readonly Dictionary<string, string> PinnedDataFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json"] =
            "97a0f5fe80c7f63d4d950776c49d15cee6a259b51958d4ac324fff1ec55349ff",
        ["src/Ripcord.Cloud.Halyard/Data/halyard-oauth-client.json"] =
            "7a7426e4c114753f934e31c83359a9151d059e0167e359987d19a8d264f6e9f0",
    };

    /// <summary>
    /// Where on a line a candidate value sits. The sweep does not treat these alike, so neither can the
    /// contract: <see cref="LongHex"/> and <see cref="SeparatedByteRun"/> see only
    /// <see cref="ProsePortion"/>, while the address, MAC and truncation patterns see the whole line.
    /// Stating the context per row is what stops the contract from certifying a catch the sweep never makes.
    /// </summary>
    public enum LineContext
    {
        /// <summary>A line of a <c>.md</c>, <c>.json</c> or <c>NOTICE</c>-class file: all of it is prose.</summary>
        Prose,

        /// <summary>A trailing <c>//</c> comment on a line of code — prose the policy reaches.</summary>
        Comment,

        /// <summary>Code with no comment: a string literal, an initialiser, a fixture array.</summary>
        Code,

        /// <summary>
        /// A hex string bound to a named <c>const</c> in a <b>test</b> file — the one shape swept for value
        /// there, because it is how the committed interoperability constants are written.
        /// </summary>
        DeclaredConstant,

        /// <summary>
        /// A line of shipping code (<c>src/</c>, <c>tools/</c>, a port's <c>source/</c>). Every hex literal
        /// of sixteen bytes or more is swept here, in any syntax, because this is the corpus a user runs.
        /// </summary>
        ProductCode,

        /// <summary>
        /// A XAML <c>x:Uid</c>. The language allows only an identifier there, so it cannot carry data —
        /// but a descriptive uid is long and drawn entirely from the base64 alphabet, which is why this
        /// is a row rather than something a reader has to infer from the absence of a finding.
        /// </summary>
        XamlIdentifier,
    }

    /// <summary>
    /// Values purged from this repository's history, identified by length and SHA-256 rather than written
    /// down. Matched with <b>no floor, no prose scoping and no allowlist</b>, in every corpus.
    ///
    /// <para><b>Why this is separate from the detector.</b> Two rewrites removed a captured frame tail, and
    /// each time a commit asserted the value was gone when it was not — the first from a one-off script
    /// that mis-parsed <c>git cat-file</c>, the second from
    /// <see cref="HistoricalBlobs_CarryNothingUnredacted"/> itself, which passed while an eight-character
    /// form of the value sat in a reachable blob, four characters under <see cref="LongHex"/>'s documented
    /// twelve. The floor is a good decision for the tree sweep and the wrong instrument here, because
    /// <em>"nothing in this looks like a secret"</em> and <em>"this specific value is gone"</em> are
    /// different propositions and only the second one was being claimed.</para>
    ///
    /// <para><b>Why hashes and not the values.</b> The obvious form of this list is literal, and a literal
    /// list would put the value back into the one file the sweep structurally cannot read — which is the
    /// finding an earlier round raised about the contract table. Storing the length and the digest keeps the
    /// list complete without reintroducing what it exists to prove absent. The cost is that a value must be
    /// hashed to be added, which is one command and is written into the failure message.</para>
    /// </summary>
    private static readonly (int Length, string Sha256)[] PurgedValues =
    [
        (16, "4293ff14142dd3be6bace0049d591d960bf0fe2545f59a63716443225351dca9"),
        (8, "4995751499b43f886e7c50af275bea59b2ed10937391e17173d133c2db41d556"),
        // The session-id frame payload, purged 2026-09-11. Committed for weeks under a comment
        // asserting it was synthetic; it was a real captured 0x0033 payload, and because that frame
        // is encrypted it was ciphertext against a plaintext the spec publishes. Caught by
        // CaptureProvenanceTests, which checks provenance claims rather than trusting them.
        (34, "2c7b51060080c732a5c57e7d2720c389e854e912a00aaefbd0607291568b0f51"),
    ];

    /// <summary>
    /// Hex, possibly written with separators, from which a purged value could be reassembled. Runs of
    /// separators are allowed, and <c>:</c> and <c>.</c> are in the class, because the question here is
    /// what a value could be <em>rewritten</em> as, not what this repository happens to write today.
    /// </summary>
    private static readonly Regex HexRun =
        new(@"[0-9a-fA-F](?:[ ,\t:._-]*[0-9a-fA-F])*", RegexOptions.Compiled);

    /// <summary>Every character <see cref="HexRun"/> will step over, and therefore every one to strip.</summary>
    private static readonly char[] HexRunSeparators = [' ', (char)0x09, ',', ':', '.', '-', '_'];

    /// <summary>
    /// Flattens a hex run to bare hex. <b>Not <see cref="Normalise"/>.</b> That function maps <c>-</c> to
    /// <c>:</c> so a dash-separated MAC canonicalises to its colon twin, which is right for the allowlist
    /// and exactly wrong here: it turned the one separator <see cref="HexRun"/> was widened to accept into
    /// a character nothing then stripped, so a dash-separated run flattened to a colon-separated one and
    /// matched nothing. Two halves of one mechanism, each validated against a different definition of "separator" —
    /// inside the check written specifically to be definition-independent.
    /// </summary>
    private static string FlattenHex(string run)
    {
        Span<char> buffer = stackalloc char[run.Length];
        int n = 0;
        foreach (char c in run)
        {
            if (!HexRunSeparators.Contains(c)) buffer[n++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..n]);
    }

    /// <summary>
    /// Any window of any hex run on this line whose digest is a purged value. Separator-blind, so a value
    /// re-spaced or re-delimited is still found, and length-blind in the sense that matters: it looks for
    /// exactly the lengths recorded, wherever they sit inside a longer run.
    /// </summary>
    private static IEnumerable<string> PurgedValueHits(string line)
    {
        foreach (Match run in HexRun.Matches(line))
        {
            string flat = FlattenHex(run.Value);
            foreach ((int length, string sha) in PurgedValues)
            {
                for (int i = 0; i + length <= flat.Length; i++)
                {
                    string window = flat.Substring(i, length);
                    string digest = Convert.ToHexString(
                        SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(window))).ToLowerInvariant();
                    if (digest == sha) yield return $"{length} chars, sha {sha[..12]}";
                }
            }
        }
    }

    /// <summary>
    /// What this sweep must catch, and what it must tolerate — <em>and where</em>. One row per gap ever
    /// found. When the next gap turns up, add the row first: the row is the finding; changing a regex is only
    /// how it gets satisfied.
    ///
    /// <para>The <see cref="LineContext"/> column exists because the previous version of this table asserted
    /// against the bare regexes rather than the sweep, so five rows — including the <c>0x</c>-prefixed one
    /// the docstring above singles out — certified a catch that does not happen in a code literal. That was the seventh instance of "a scope narrower than the belief held
    /// about it", and the first where the belief was written down in a test that passed. The
    /// <see cref="LineContext.Code"/> rows below make the prose-only trade a decision on the record rather
    /// than an absence.</para>
    /// </summary>
    // Everything to the closing marker is deliberately leak-shaped and is not swept.
    // <sweep:fixtures>
    public static TheoryData<string, LineContext, bool, string> DetectorContract() => new()
    {
        // --- must catch in prose: shapes that have concealed a real value at some point ----------------
        { "0xdeadbeefcafebabe1234", LineContext.Prose, true, "the 0x prefix defeated the old \\b anchor" },
        { "deadbeefcafebabe1234", LineContext.Prose, true, "plain long hex" },
        { "0x4825abcd…", LineContext.Prose, true, "0x plus truncation" },
        { "4825abcd…", LineContext.Prose, true, "a truncated prefix - the shape of every leak found in round 1" },
        { "…fddb4c", LineContext.Prose, true, "suffix truncation, the mirror shape" },
        { "AA:BB:CC:DD:EE:FF", LineContext.Prose, true, "MAC, uppercase" },
        { "00-11-22-33-44-55", LineContext.Prose, true, "MAC, dash-separated" },
        { "001122334455aa", LineContext.Prose, true, "bare hex below the old 16-character floor" },
        { "b7 4e 2c 91 6a d3 58 0f", LineContext.Prose, true, "space-separated byte run" },
        { "b7 4e 2c 91 6a d3", LineContext.Prose, true, "six bytes - a key wrapped across lines yields short runs" },
        { "b7\t4e\t2c\t91\t6a\td3\t58", LineContext.Prose, true, "tab-separated byte run" },
        { "b7,4e,2c,91,6a,d3,58", LineContext.Prose, true, "comma-separated byte run" },
        { "172.217.16.14", LineContext.Prose, true, "public IPv4" },
        { "2600:1f18:1a2b:3c4d:5e6f:7a8b:9c0d:1e2f", LineContext.Prose, true, "global IPv6, uncompressed" },
        { "2600:1700:abcd::5", LineContext.Prose, true, "global IPv6, compressed - how a real address is actually written" },
        { "fe80::1c2d:3e4f:5a6b:7c8d", LineContext.Prose, true, "link-local, compressed" },

        // --- must catch in a trailing comment: the policy reaches a comment wherever it sits -----------
        { "0xdeadbeefcafebabe1234", LineContext.Comment, true, "a trailing comment is prose; ProsePortion must return it" },
        { "b7 4e 2c 91 6a d3 58 0f", LineContext.Comment, true, "a captured-frame shape in a trailing comment - the 3DS port carried one exactly here" },
        { "2600:1700:abcd::5", LineContext.Comment, true, "an address is caught wherever it appears" },

        // --- must catch in code: patterns that deliberately ignore the prose boundary ------------------
        { "AA:BB:CC:DD:EE:FF", LineContext.Code, true, "a MAC is a MAC in a fixture too - SeparatedMac runs on the whole line" },
        { "172.217.16.14", LineContext.Code, true, "addresses are never prose-scoped" },
        { "fe80::1c2d:3e4f:5a6b:7c8d", LineContext.Code, true, "addresses are never prose-scoped" },
        { "4825abcd…", LineContext.Code, true, "an ellipsis in code is redacted output, not a fixture" },

        // --- must TOLERATE in code: the prose-only scope, stated as a decision -------------------------
        // Sweeping bare code lines for long hex puts every crypto fixture, test vector and KDF table in
        // this repository into scope, which is the noise that gets a guard switched off. The cost is real
        // and is paid deliberately: a capture-derived constant assigned to a string literal is invisible
        // here. That is why the three that exist are pinned by value instead - see PinnedDataFiles and
        // PinnedConstants, which are the compensating control for exactly these four rows.
        { "0xdeadbeefcafebabe1234", LineContext.Code, false, "long hex in a code literal is out of scope by decision, not by accident" },
        { "deadbeefcafebabe1234", LineContext.Code, false, "same trade: a bare fixture is not swept for value" },
        { "001122334455aa", LineContext.Code, false, "same trade" },
        { "b7 4e 2c 91 6a d3 58 0f", LineContext.Code, false, "a byte-array initialiser is the archetype of the noise this excludes" },

        // --- must catch in a declaration: the one exception to the prose-only scope --------------------
        { "a3f19c4e0d86a7b0430d8cdb78070b4c55a2e6f81b9d3c07a4e5f60918273645", LineContext.DeclaredConstant, true,
          "a 32-byte constant in a string literal - exactly how ClientTypeHex is written, and it was invisible" },
        { "a3f19c4e0d86a7b0430d8cdb78070b4c", LineContext.DeclaredConstant, true, "16 bytes, the floor" },
        { "deadbeefcafebabe1234", LineContext.DeclaredConstant, false,
          "20 characters - under the 16-byte floor, a trade against 61 distinct crypto vectors in tests" },

        // --- must catch in shipping code: every syntax, because the corpus costs nothing to sweep whole ---
        // Six of these walked past the declaration-shaped rule that preceded CodeHexLiteral. The wrapped
        // form is the one that mattered: .editorconfig caps lines at 120, so a constant any larger than the
        // 107-character ClientTypeHex is wrapped by anyone following the project's own style, and the
        // per-line scan could not follow it. Each row is one of those forms.
        // The 32-byte fixture below is synthetic, generated for this table and tied to no console, account
        // or session — stated explicitly because every other leak-shaped fixture in this repository carries
        // that declaration, and a reader should not have to infer it from Excluded()'s note that this file
        // is deliberately leak-shaped.
        { "a3f19c4e0d86a7b0430d8cdb78070b4c55a2e6f81b9d3c07a4e5f60918273645", LineContext.ProductCode, true,
          "a synthetic 32-byte constant in shipping code - the ClientTypeHex shape, and this rule's whole reason" },
        { "a3f19c4e0d86a7b0430d8cdb78070b4c", LineContext.ProductCode, true, "16 bytes, the floor" },
        { "        \"a3f19c4e0d86a7b0430d8cdb78070b4c55a2e6f81b9d3c07a4e5f60918273645\";",
          LineContext.Prose, true, "the value wrapped onto its own line - what the 120-column rule produces" },

        // --- must tolerate: the boundary itself, stated so it reads as a decision -----------------------
        // A hex literal in a test file is swept only when it is a const declaration. 83 sites and 61
        // distinct values live there, essentially all published NIST vectors and captured protocol frames;
        // inverting over them would add 61 entries reading "a crypto test vector" and bury the 28 whose
        // reasons are the actual guard. The risk being defended against is a constant shipped to users, and
        // that corpus is swept without exception. This is the gap, and it is chosen.
        { "a3f19c4e0d86a7b0430d8cdb78070b4c55a2e6f81b9d3c07a4e5f60918273645", LineContext.Code, false,
          "a bare literal in a test file is out of value scope - the accepted half of the trade above" },

        // --- XAML identifier attributes: long, base64-alphabet, and structurally incapable of data ----
        { "SettingsPage_CredentialsAreProtectedWithDpapi", LineContext.XamlIdentifier, false,
          "a descriptive x:Uid is 40+ base64-alphabet characters and is an identifier, not a value" },
        { "SettingsPage_CredentialsAreProtectedWithDpapi", LineContext.ProductCode, true,
          "the same text as a bare literal is not an identifier attribute and is reported" },

        // --- must tolerate: things that are not disclosures --------------------------------------------
        { "and so on, continued…", LineContext.Prose, false, "an ordinary prose ellipsis" },
        { "192.0.2.1", LineContext.Prose, false, "RFC 5737 documentation address" },
        { "10.0.0.7", LineContext.Prose, false, "RFC 1918, published as captured by stated policy" },
        { "224.0.0.251", LineContext.Prose, false, "the mDNS multicast group" },
        { "Version=\"1.0.0.0\"", LineContext.Prose, false, "a version quad, not an address - decided rather than discovered" },
        { "ITU-T H.264 sec 7.4.1.1", LineContext.Prose, false,
          "a four-part specification clause is not an address - ripcord-ps3 is full of them" },
        { "the console answered from 172.217.16.14 in section 3", LineContext.Prose, true,
          "a citation word with no dotted clause after it must not suppress a real address" },
        { "fd00:1a2b:3c4d:5e6f:0011:2233:4455:6677", LineContext.Prose, false, "the declared synthetic ULA, allowed by value" },
        { "fd00:abcd:1234:5678::9", LineContext.Prose, true, "a different address in the same /8 - the prefix test used to wave this through" },
        { "2001:db8::1", LineContext.Prose, false, "RFC 3849 documentation prefix" },
        { "2001:db8a::1", LineContext.Prose, true, "2001:db8a::/32 is not RFC 3849 - the prefix test was not colon-terminated" },
        { "00000000000000000000", LineContext.Prose, false, "filler, not a value" },
    };
    // </sweep:fixtures>

    /// <summary>
    /// Values that are not in a file the sweep can usefully read, pinned by the SHA-256 of the value itself.
    ///
    /// <para><c>ClientTypeHex</c> is the third declared committed constant (CLAUDE.md and <c>NOTICE</c> both
    /// carry it), and until this pin it was the only one with no value-level guard of any kind: it lives in a
    /// C# string literal with no comment, so the prose-only scope above cannot see it, and the two tests that
    /// name it assert how a message is built rather than what the value is. The hash is of the constant's
    /// text, so the value is not restated here.</para>
    /// </summary>
    private static readonly Dictionary<string, (string Sha256, Func<string> Read)> PinnedConstants = new()
    {
        ["HalyardRegistrationMessage.ClientTypeHex"] =
            ("426f45089cb8f0af6360e69974ba40dc249b60d8a3c98a7d6d5eac232969b010",
             () => Ripcord.Protocol.Halyard.Common.Crypto.HalyardRegistrationMessage.ClientTypeHex),
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

    /// <summary>
    /// Build scratch and the dirty room. The doubled dot in <c>Ripcord..</c> is deliberate and is not a typo
    /// for a project directory: MSBuild names its per-project tracking-log folder
    /// <c>&lt;Project&gt;..&lt;hash&gt;</c> — e.g. <c>src/Ripcord.Input.Interop/Ripcord..7A5F5E4B/</c> — which
    /// exists only after a native build and so is invisible in a clean clone. Written with one dot it would
    /// silently drop every project directory under <c>src/</c> and <c>tests/</c>, which is most of the corpus.
    /// </summary>
    /// <summary>
    /// Lines between these markers are not swept. They bracket <see cref="DetectorContract"/>, which has
    /// to hold leak-shaped fixtures to do its job.
    ///
    /// <para>This replaced a whole-file exclusion. The exemption was needed for a table and was granted to
    /// 1,300 lines, so every docstring here — including the paragraphs explaining what the guards are for
    /// — sat outside every corpus. The predictable happened: a purged value went into the prose above in
    /// the same commit that wrote "illustrate the new form with a synthetic value, never the real one"
    /// into CONTRIBUTING.md, and no check could see it. An exemption drifting from its reason is this
    /// file's oldest failure shape; here it had drifted by three orders of magnitude.</para>
    /// </summary>
    private const string FixturesBegin = "// <sweep:fixtures>";
    private const string FixturesEnd = "// </sweep:fixtures>";

    /// <summary>
    /// How many fixture regions this file has. Asserted, not assumed: a marker mechanism that silently
    /// tolerates the wrong number of regions is a mechanism that stops looking without saying so.
    /// </summary>
    private const int ExpectedFixtureRegions = 2;

    /// <summary>
    /// Which lines of the sweep's own file are exempt, and what is wrong with the markers if anything is.
    ///
    /// <para><b>Fails closed, deliberately, and every clause here is a defect that existed.</b> The first
    /// version of this mechanism was a bare boolean toggled by <c>Contains</c>, honoured in every swept
    /// file. So: any committed file could exempt its own content by carrying the string — an exemption
    /// meant for two tables, granted to anything that asked. Dropping a closing marker during an edit
    /// silently exempted the rest of the file and the suite stayed green, because the guard simply stopped
    /// looking. And <c>Contains</c> fired on any <em>mention</em>, so documenting the convention would have
    /// opened an unswept region inside the document explaining how not to open one.</para>
    ///
    /// <para>The reason for the exemption is "this file's two tables", so the mechanism says exactly that:
    /// caller-gated to this file, whole-line markers rather than substrings, and a count that must match.
    /// Anything else is reported as an offender rather than tolerated. Three rounds running, the instance
    /// was fixed and the mechanism inherited more generality than the reason required; this is the attempt
    /// to write the reason in instead.</para>
    /// </summary>
    private static (bool[] Exempt, string? Problem) FixtureRegions(string[] lines)
    {
        bool[] exempt = new bool[lines.Length];
        int open = -1;
        int pairs = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();

            if (trimmed == FixturesBegin)
            {
                if (open >= 0) return (exempt, $"fixture marker reopened at line {i + 1} without closing the one at line {open + 1}");
                open = i;
            }
            else if (trimmed == FixturesEnd)
            {
                if (open < 0) return (exempt, $"fixture marker closed at line {i + 1} without being opened");
                for (int j = open; j <= i; j++) exempt[j] = true;
                open = -1;
                pairs++;
            }
        }

        if (open >= 0) return (exempt, $"fixture marker opened at line {open + 1} is never closed, so the rest of the file would go unswept");
        if (pairs != ExpectedFixtureRegions) return (exempt, $"expected {ExpectedFixtureRegions} fixture regions, found {pairs}");

        return (exempt, null);
    }

    /// <summary>
    /// The sweep's own file, which is swept like any other except for the fixture block.
    ///
    /// <para>Still excluded wholesale from the <em>historical</em> corpus, and that is deliberate rather
    /// than lazy: revisions predating the markers have leak-shaped fixtures spread through the file, so
    /// sweeping them would report the contract table of every past version. History cannot be given
    /// markers retroactively without a rewrite per revision.</para>
    ///
    /// <para><b>The consequence, which is the audit-relevant half:</b> three historical blobs of this file
    /// carry a dash-separated form of a purged value, put there by a docstring written before the prose
    /// here was swept. Eight characters of a ciphertext prefix that identify nothing — but no guard in
    /// this repository can see them, and a reader of this file should learn that from this file rather
    /// than from an outside review. Removing them is one <c>filter-repo --replace-text</c> pass and is
    /// cheap only while the repository is unpublished.</para>
    /// </summary>
    private static bool IsSweepOwnFile(string relative) =>
        relative.EndsWith("PublishedTreeSweepTests.cs", StringComparison.Ordinal);

    private static bool Excluded(string relative) =>
        relative.Split('/').Any(s =>
            s is "captures" or "bin" or "obj" or "build" or ".git" or ".vs" or ".claude"
              or "node_modules" or "packages" or "Generated Files" or "ARM64" or "x64"
            || s.StartsWith("Ripcord..", StringComparison.Ordinal));

    /// <summary>
    /// Code that ships, as opposed to code that tests it. The distinction carries real weight: a hex
    /// constant under <c>src/</c> is something every user runs, while one in a test file is almost always a
    /// published NIST vector or a captured frame the parser is checked against. Ports keep their tests
    /// beside their source, so the ports clause names a segment rather than a prefix.
    ///
    /// <para><b>It names the test segment, not the source one, and the difference is the whole point.</b>
    /// This clause read <c>Contains("/source/")</c> until the portable core was extracted to
    /// <c>ports/common/</c> — whose files are <c>crypto/</c>, <c>takion/</c>, <c>stream/</c>,
    /// <c>session/</c>, with no <c>/source/</c> segment anywhere. Ninety files, the crypto and the key
    /// schedule and the transport among them, silently reclassified as test code: the strict
    /// <see cref="CodeHexLiteral"/> rule stopped applying to them, <see cref="CodeBase64Literal"/> stopped
    /// running on them entirely, and nothing said so. What surfaced it was
    /// <see cref="EveryAllowlistEntry_SuppressesSomethingReal"/> reporting the base64-alphabet entry as
    /// inert — the entry had been written for <c>rc_base64.c</c>, which had simply moved.</para>
    ///
    /// <para>A positive marker fails open: code in a layout the rule did not anticipate gets the weaker
    /// scope, and gets it quietly. A negative one fails closed — anything new under <c>ports/</c> is
    /// product code until a <c>tests</c> segment says otherwise, so the next port to invent a directory
    /// layout is over-covered rather than under-covered. For a guard, that is the direction to be wrong in.
    /// <see cref="ProductCodeClassification"/> pins both halves.</para>
    /// </summary>
    private static bool IsProductCode(string relative) =>
        relative.StartsWith("src/", StringComparison.Ordinal)
        || relative.StartsWith("tools/", StringComparison.Ordinal)
        || (relative.StartsWith("ports/", StringComparison.Ordinal)
            && !relative.Split('/').Contains("tests", StringComparer.Ordinal));

    /// <summary>
    /// The files this sweep reads: the ones git actually tracks.
    ///
    /// <para><b>It asked the filesystem until 2026-09-12, and the two answers are not the same.</b> A
    /// <c>Directory.EnumerateFiles</c> walk filtered by <see cref="Excluded"/> reads whatever happens to be
    /// on the disk, including files <c>.gitignore</c> exists to keep out of the repository. What surfaced
    /// it was a PS3 decoder build: <c>ports/*/third-party/</c> is gitignored, and 165 MB of upstream Mbed
    /// TLS landed there, whereupon the sweep reported upstream's DHM test constants and an address in one
    /// of its comments as "unredacted values in committed text". None of it was committed, or ever could
    /// be.</para>
    ///
    /// <para>That is not a cosmetic mismatch. A guard that cries wolf about files nobody can publish gets
    /// its findings skimmed, and this one's whole value is that a finding means something. Worse, the two
    /// halves of this file disagreed about the word "committed": <see cref="EveryCommittedTextFile_IsSwept"/>
    /// already asked <c>git ls-files</c>, and its own docstring says only git can answer which committed
    /// files a corpus does not open. The corpus it was checking answered a different question.</para>
    ///
    /// <para><b>This narrows the sweep, so the safety of the narrowing is the thing to check, not the
    /// tidiness.</b> The threat is publication, and a gitignored file is not published. The capture-type
    /// guard is unaffected because it never lived here: <see cref="NeverCommitted"/> is enforced inside
    /// <see cref="EveryCommittedTextFile_IsSwept"/>, against git's list, where a gitignored capture is
    /// correct and a committed one fails. The dirty room stays doubly covered — gitignored, and named in
    /// <see cref="Excluded"/>. What is given up is the sweep noticing a value in a file that is not in the
    /// repository and is not going to be, which was never the promise.</para>
    ///
    /// <para>If git cannot answer — an archive rather than a clone — this falls back to the old walk
    /// rather than sweeping nothing. Over-reporting is the safe direction to fail in, and an empty corpus
    /// would make every assertion in this file pass while checking nothing.
    /// <see cref="SweptCorpus_ContainsOnlyTrackedFiles"/> pins the property.</para>
    /// </summary>
    private static IEnumerable<string> CommittedText()
    {
        string root = RepoRoot();
        string? listing = RunGit(root, "ls-files");

        IEnumerable<string> relatives = listing is null
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            : listing.Split((char)0x0a).Select(r => r.TrimEnd((char)0x0d));

        foreach (string rel in relatives)
        {
            if (rel.Length == 0) continue;
            if (Excluded(rel)) continue;
            if (PinnedDataFiles.ContainsKey(rel)) continue;   // covered by hash instead

            if (!TextExtensions.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase)
                && !NamedFiles.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Tracked but not on disk: a file deleted in the working tree is still in git's list, and
            // reading it would throw rather than report anything.
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;

            yield return full;
        }
    }

    /// <summary>The dictionary folds case; there is no second clause, and so no doubt that it does.</summary>
    /// <summary>
    /// Whether a value is tolerated <em>in this corpus</em>. The scoping is the point: splitting the
    /// allowlist so reachability could be asserted per corpus, while suppression still consulted both
    /// dictionaries everywhere, widened the file allowlist by the history-evidenced entries, which nothing
    /// checks against files. One of them was the real captured ciphertext — so had it reappeared in a source
    /// file, the file sweep would have silently swallowed it.
    ///
    /// <para>That is the fifth appearance of this file's signature defect: an allowlist validated against
    /// something other than what it suppresses in. Message-corpus entries stay readable from the message
    /// sweep only, because commit messages legitimately quote values the docs carry.</para>
    /// </summary>
    private static bool IsAllowed(string value, bool inMessages)
    {
        string n = Normalise(value);
        return Allowed.ContainsKey(n) || (inMessages && AllowedInMessages.ContainsKey(n));
    }

    /// <summary>
    /// A declared constant already covered by <see cref="PinnedConstants"/>. Not an allowlist entry, because
    /// the point is not that the value is tolerable — it is that a stronger guard has it.
    ///
    /// <para>Matched against the <em>recorded hash</em>, not the constant's current value. Reading the live
    /// value would make the exemption self-referential: swap the constant for a per-console one and it would
    /// still exempt itself, leaving the pin as the only guard. Keyed on the hash, a changed value loses the
    /// exemption and fails both.</para>
    /// </summary>
    private static bool IsPinnedConstant(string hex)
    {
        string sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hex)))
            .ToLowerInvariant();
        return PinnedConstants.Values.Any(c => c.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase));
    }

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
        // A quad that does not parse is not an address, so it is tolerated rather than reported. Stated
        // because it is the permissive branch: `999.1.2.3` passes here, and nothing it could disclose is
        // reachable over IP.
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

    /// <summary>
    /// The one synthetic ULA is allowed <em>by value</em>, not by prefix. <c>fd00:</c> is a whole /8, and ULA
    /// is exactly the family in which the console's real token was recorded — a router that skips the
    /// randomisation RFC 4193 asks for emits `fd00:` addresses, so a prefix test would wave through the very
    /// thing this looks for. RFC 3849's test is colon-terminated for the same reason: <c>2001:db8abc::1</c> is
    /// not a documentation address.
    /// </summary>
    private static bool IsAllowedIpv6(string value)
    {
        string v = value.ToLowerInvariant();
        return v is "fd00:1a2b:3c4d:5e6f:0011:2233:4455:6677"      // the declared synthetic, exactly
            || v.StartsWith("2001:db8:", StringComparison.Ordinal)  // RFC 3849, incl. the "2001:db8::x" form
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

    /// <summary>
    /// The whole detector, for one line. Everything that reports lives here and nowhere else, so the file
    /// sweep and the contract cannot drift apart: that drift is what let the contract certify five catches
    /// the sweep did not perform.
    /// </summary>
    private static List<string> ScanLine(
        string line, bool isProse, bool applyAllowlist, string at, bool isProductCode = false,
        bool isMessage = false)
    {
        bool Allow(string v) => applyAllowlist && IsAllowed(v, isMessage);

        List<string> found = [];

        // First, and unconditionally. No floor, no scoping, no allowlist: this asks whether a value known
        // to have been purged from this repository is still present, and that question has no legitimate
        // "yes". Everything below it answers the different question of whether anything *looks* like a
        // secret, and every floor and scope those rules carry is a deliberate trade that does not belong
        // in a completeness check.
        foreach (string hit in PurgedValueHits(line))
        {
            found.Add($"{at}|purged-value|{hit}");
        }

        foreach (Match m in TruncatedHex.Matches(line))
        {
            if (Allow(m.Groups[1].Value)) continue;
            found.Add($"{at}|truncated|{m.Groups[1].Value}");
        }

        foreach (Match m in SuffixTruncatedHex.Matches(line))
        {
            if (Allow(m.Groups[1].Value)) continue;
            found.Add($"{at}|suffix|{m.Groups[1].Value}");
        }

        string prose = isProse ? line : ProsePortion(line);
        if (prose.Length > 0)
        {
            foreach (Match m in LongHex.Matches(prose))
            {
                string hex = m.Groups[1].Value;
                if (IsSyntheticFiller(hex)) continue;
                if (Allow(hex)) continue;
                found.Add($"{at}|hex|{hex}");
            }

            foreach (Match m in SeparatedByteRun.Matches(prose))
            {
                string flat = Normalise(m.Value);
                if (IsSyntheticFiller(flat)) continue;
                if (Allow(flat)) continue;
                found.Add($"{at}|byte-run|{flat}");
            }
        }

        foreach (Match m in (isProductCode ? CodeHexLiteral : DeclaredHexConstantInTests).Matches(line))
        {
            string hex = m.Groups[1].Value;
            if (IsSyntheticFiller(hex)) continue;
            if (IsPinnedConstant(hex)) continue;   // guarded by hash instead, like the two data files
            if (Allow(hex)) continue;
            found.Add($"{at}|{(isProductCode ? "shipped-hex" : "declared-const")}|{hex}");
        }

        if (isProductCode)
        {
            foreach (Match m in CodeBase64Literal.Matches(line))
            {
                string b64 = m.Groups[1].Value;
                if (PureHex.IsMatch(b64)) continue;   // the hex rule above already reported it

                // A XAML x:Uid, x:Name or x:Key is an identifier by the language's own rules - it cannot
                // hold a secret, because it cannot hold anything but an identifier. Long descriptive uids
                // are ordinary here and every one of them is drawn from the base64 alphabet. This is a
                // property of the attribute, not an exemption for a place: nothing else on the line is
                // skipped, and the hex rule still applies to all of it.
                if (XamlIdentifierAttribute.IsMatch(line[..m.Index])) continue;
                if (IsSyntheticFiller(b64)) continue;
                if (Allow(b64)) continue;
                found.Add($"{at}|shipped-base64|{b64}");
            }
        }

        foreach (Match m in SeparatedMac.Matches(line))
        {
            if (Allow(m.Value)) continue;
            found.Add($"{at}|mac|{m.Value}");
        }

        foreach (Match m in Ipv4.Matches(line))
        {
            if (IsDocumentationAddress(m.Groups[1].Value)) continue;
            if (NotAnAddressContext.IsMatch(line)) continue;
            found.Add($"{at}|ipv4|{m.Value}");
        }

        foreach (Match m in Ipv6.Matches(line))
        {
            if (SeparatedMac.IsMatch(m.Value)) continue;   // a MAC, not an address
            if (IsAllowedIpv6(m.Value)) continue;
            if (Allow(m.Value)) continue;   // this family had no allowlist hook
            found.Add($"{at}|ipv6|{m.Value}");
        }

        return found;
    }

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

            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            bool isProductCode = IsProductCode(relative);

            string[] lines = File.ReadAllLines(path);

            // Only this file's markers mean anything. Anywhere else the string is just text.
            bool[] exempt = new bool[lines.Length];
            if (IsSweepOwnFile(relative))
            {
                (exempt, string? problem) = FixtureRegions(lines);
                if (problem is not null) found.Add($"{relative}|fixture-markers|{problem}");
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (exempt[i]) continue;

                found.AddRange(
                    ScanLine(lines[i], isProse, applyAllowlist, Where(root, path, i + 1), isProductCode));
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

            // Line endings normalised before hashing. Hashing the bytes as they sit on disk pinned the
            // checkout rather than the content: `.gitattributes` stores these LF and checks them out
            // native, so the same unmodified file hashed one way on Windows and another on Linux. The
            // pin passed on the author's machine and failed every CI run on Linux and macOS for weeks.
            // LF is the stored form, so the LF hash is the repository's content, whatever a checkout does.
            byte[] bytes = File.ReadAllBytes(path);
            string actual = Convert.ToHexString(SHA256.HashData(Normalised(bytes))).ToLowerInvariant();
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

    /// <summary>
    /// Every commit message in the history, through the same detector in prose mode.
    ///
    /// <para><b>Why this exists.</b> Every other check here reads files, and a commit message is neither a
    /// file nor less published than one: it is permanent, project-authored prose that ships with the clone,
    /// and nothing could see it. That was not hypothetical. Eight bytes of captured frame tail were redacted
    /// from a 3DS source comment on the grounds that they are session-keyed, and the same bytes stayed in
    /// the message of the commit that did the redacting, where no edit short of rewriting history reaches
    /// them without a rewrite. Nothing here noticed for three weeks, and that is the finding — not the
    /// bytes, which were eight bytes of non-reversible ciphertext identifying nothing. They were carried as
    /// an allowlisted residue for two rounds and then removed at the source: the repository had never been
    /// public, so the rewrite that the residue's own reasoning had priced as expensive was in fact free, and
    /// the message now describes the shape exactly as the code comment beside it does. The entries that
    /// remain here are synthetic values quoted by later commit messages.</para>
    ///
    /// <para>The value of this check is timing. Caught before a push, a bad message is one
    /// <c>git commit --amend</c>; caught after, it is a history rewrite, which this project has done enough
    /// times to know the price. Returns empty rather than failing when git is unavailable — a source archive
    /// is a legitimate way to receive this repository, and it simply has no such corpus.</para>
    /// </summary>
    /// <summary>
    /// Whether the message corpus is readable here, and if not, why. <c>Reason</c> is null when it is.
    ///
    /// <para>A shallow clone is the case that matters and the one that nearly got away. <c>git log</c> in a
    /// <c>--depth 1</c> checkout returns one commit and exits <b>0</b>: the sweep succeeds, reads 1 of 200-odd
    /// messages and reports clean, and success and near-total failure are the same observable.
    /// <c>actions/checkout</c> defaults to exactly that. The other two cases — no <c>.git</c>, no
    /// <c>git</c> — are honest absences and return quietly, because a source archive is a legitimate way to
    /// receive this repository and simply has no such corpus.</para>
    /// </summary>
    private static (bool Readable, string? Reason) MessageCorpusState()
    {
        string root = RepoRoot();

        // Either form counts. A worktree or a submodule has .git as a *file* pointing at the real git dir,
        // and testing only for a directory short-circuited before rev-parse could ever run — so in a
        // `git worktree`, a mainstream workflow, the sweep did nothing and said nothing. That is the exact
        // silent pass this function exists to prevent, one environment over. The guard stays because it
        // correctly stops an archive unpacked inside some other repository from sweeping *that* history.
        string dotGit = Path.Combine(root, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit)) return (false, null);
        string? shallow = RunGit(root, "rev-parse --is-shallow-repository");
        if (shallow is null) return (false, null);

        if (shallow.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
            || (Directory.Exists(dotGit) && File.Exists(Path.Combine(dotGit, "shallow"))))
        {
            return (false,
                "This is a SHALLOW clone, so the commit-message corpus is truncated — `git log` here answers "
                + "successfully with a fraction of the history, which is why this fails instead of passing. "
                + "In CI, set `fetch-depth: 0` on the actions/checkout step (the default is 1). Locally, "
                + "`git fetch --unshallow`.");
        }

        return (true, null);
    }

    /// <summary>Runs git and returns stdout, or null if it could not be run or exited non-zero.</summary>
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
                //
                // The first draft of this very comment illustrated the point by quoting one of those
                // fragments, and the file sweep caught it - which is the second half of the lesson and the
                // reason it is written down rather than demonstrated.
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

    private static List<string> CommitMessageOffenders(bool applyAllowlist = true)
    {
        string root = RepoRoot();
        if (!MessageCorpusState().Readable) return [];

        string? log = RunGit(root, "log --no-color --format=%H%x1f%B%x1e");
        if (log is null) return [];

        const char RecordSeparator = (char)0x1e;
        const char UnitSeparator = (char)0x1f;
        const char Lf = (char)0x0a;
        const char Cr = (char)0x0d;

        List<string> found = [];
        foreach (string record in log.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = record.Split(UnitSeparator, 2);
            if (parts.Length != 2) continue;

            string sha = parts[0].Trim();
            sha = sha[..Math.Min(8, sha.Length)];

            string[] lines = parts[1].Split(Lf);
            for (int i = 0; i < lines.Length; i++)
            {
                found.AddRange(
                    ScanLine(
                        lines[i].TrimEnd(Cr), isProse: true, applyAllowlist, $"commit {sha}:{i + 1}",
                        isMessage: true));
            }
        }

        return found;
    }

    /// <summary>
    /// Every blob that has ever been committed, swept with the rules for the path it lived at.
    ///
    /// <para><b>Why this exists.</b> A clone carries the whole history, so a value redacted from a file is
    /// still shipped by the revision before the redaction. This project has hit that six times, and the
    /// sixth was the worst kind: a rewrite removed eight bytes of captured ciphertext from every commit
    /// <em>message</em>, the commit doing it was titled "now that the value is gone from the history", and
    /// the bytes stayed in nine historical blobs. The check that was supposed to confirm the purge was
    /// written for the occasion, mis-parsed <c>git cat-file --batch</c>, and reported zero. An outside
    /// reviewer found it.</para>
    ///
    /// <para>So the rule this encodes is not "redact the file" but <b>a value is gone when no reachable
    /// object contains it</b>. It is the check that reviewer had been running by hand for nine rounds; it
    /// costs about a second, and having it here means the next redaction is verified by the same detector
    /// that defines what needs redacting, rather than by a one-off script written under the impression the
    /// job is already done.</para>
    ///
    /// <para>Absent history is an honest absence and returns empty, exactly as the message sweep does — but
    /// a shallow clone is not, and takes the same loud failure, for the same reason.</para>
    /// </summary>
    private static List<string> HistoricalBlobOffenders()
    {
        string root = RepoRoot();
        if (!MessageCorpusState().Readable) return [];

        string? listing = RunGit(root, "rev-list --all --objects");
        if (listing is null) return [];

        // One path per blob, because `rev-list --all --objects` names each object once even when the
        // same content lived at several paths — `pch.cpp` and `dllmain.def` are each shared by the two
        // interop projects here. That single name then decides four things: exclusion, pin membership,
        // extension eligibility and product-code status. A blob shared between a swept path and an
        // excluded one would therefore be classified by whichever name git happened to emit first.
        // Known, latent (the shared blobs here are swept under every one of their names), and the fix if
        // it ever bites is to build the map from `log --all --raw` instead, which is a great deal slower.
        Dictionary<string, string> pathOf = [];
        foreach (string line in listing.Split((char)0x0a))
        {
            string[] parts = line.TrimEnd((char)0x0d).Split(' ', 2);
            if (parts.Length == 2 && parts[1].Length > 0) pathOf.TryAdd(parts[0], parts[1]);
        }

        Dictionary<string, string> wanted = [];
        foreach ((string sha, string rel) in pathOf)
        {
            if (Excluded(rel) || IsSweepOwnFile(rel)) continue;   // see IsSweepOwnFile: history has no markers
            if (PinnedDataFiles.ContainsKey(rel)) continue;

            string e = Path.GetExtension(rel);
            if (TextExtensions.Contains(e, StringComparer.OrdinalIgnoreCase)
                || NamedFiles.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase))
            {
                wanted[sha] = rel;
            }
        }

        List<string> found = [];
        foreach ((string sha, string body) in CatFileBatch(root, wanted.Keys))
        {
            string rel = wanted[sha];
            string ext = Path.GetExtension(rel);

            bool isProse = ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
                           || NamedFiles.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase);
            bool isProductCode = IsProductCode(rel);

            string[] lines = body.Split((char)0x0a);
            for (int i = 0; i < lines.Length; i++)
            {
                found.AddRange(ScanLine(
                    lines[i].TrimEnd((char)0x0d), isProse, applyAllowlist: true,
                    $"{sha[..8]} {rel}:{i + 1}", isProductCode));
            }
        }

        return found;
    }

    /// <summary>
    /// Reads many objects through a single <c>git cat-file --batch</c>. One process per blob was the
    /// obvious way to write this and cost fifteen seconds against a suite that otherwise runs in two;
    /// batching is the difference between a guard people keep and a guard people delete. Content is decoded
    /// as Latin-1, which is a lossless byte-to-char mapping and cannot throw on a non-UTF-8 blob — the
    /// patterns are all ASCII, so nothing is lost by it.
    /// </summary>
    private static IEnumerable<(string Sha, string Body)> CatFileBatch(string root, IEnumerable<string> shas)
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "cat-file --batch",
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        if (git is null) yield break;

        // Feed stdin on another thread. Writing it all first deadlocks: git starts filling the stdout pipe
        // long before the last sha is written, that pipe's buffer fills, git blocks on it, and we are still
        // blocked writing stdin. It hangs forever rather than failing, which took a 600-second timeout to
        // notice.
        var feeder = System.Threading.Tasks.Task.Run(() =>
        {
            foreach (string sha in shas) git.StandardInput.Write(sha + (char)0x0a);
            git.StandardInput.Close();
        });

        using var raw = new MemoryStream();
        git.StandardOutput.BaseStream.CopyTo(raw);
        feeder.Wait(300_000);
        git.WaitForExit(300_000);

        byte[] all = raw.ToArray();
        int i = 0;
        while (i < all.Length)
        {
            int nl = Array.IndexOf(all, (byte)0x0a, i);
            if (nl < 0) yield break;

            string[] header = System.Text.Encoding.ASCII.GetString(all, i, nl - i).Split(' ');
            if (header.Length != 3 || !int.TryParse(header[2], out int size)) yield break;

            if (header[1] == "blob")
            {
                yield return (header[0], System.Text.Encoding.Latin1.GetString(all, nl + 1, size));
            }

            i = nl + 1 + size + 1;
        }
    }

    /// <summary>
    /// Every committed file is either swept, or a declared binary. Nothing is outside both.
    ///
    /// <para>This exists because the corpus lists above were wrong for eight rounds and no amount of
    /// reading them showed it. <c>.cpp</c>, <c>.idl</c> and <c>.def</c> were simply absent, so the whole
    /// C++/WinRT interop layer — fourteen files under <c>src/</c> — was never opened by any sweep, while
    /// the coverage of every other corpus was argued in detail. The reviewer who found it put the lesson
    /// better than the fix: the question is not "does this list look complete" but "which committed files
    /// does it not open", and only <c>git</c> can answer that.</para>
    ///
    /// <para>So a new extension arriving in the repository now fails here until someone classifies it,
    /// which is the property the list itself could never have.</para>
    ///
    /// <para>It also checks the two ways a classification can be wrong rather than missing, because the
    /// round after this guard landed found both: a file <em>declared binary</em> that is really text is
    /// hidden just as thoroughly as one on no list, so declarations are verified with a NUL-byte test
    /// rather than believed; and a capture type has no legitimate classification at all, so
    /// <see cref="NeverCommitted"/> fails outright instead of resolving to skip.</para>
    /// </summary>
    [Fact]
    public void EveryCommittedTextFile_IsSwept()
    {
        string root = RepoRoot();
        string? listing = RunGit(root, "ls-files");
        if (listing is null) return;   // no git: an archive has no committed-file list to check against

        List<string> unclassified = [];
        List<string> mislabelled = [];
        List<string> forbidden = [];
        foreach (string raw in listing.Split((char)0x0a))
        {
            string rel = raw.TrimEnd((char)0x0d);
            if (rel.Length == 0 || Excluded(rel)) continue;

            string ext = Path.GetExtension(rel);

            if (Extensions(rel).Any(e => NeverCommitted.Contains(e, StringComparer.OrdinalIgnoreCase)))
            {
                forbidden.Add(rel);
                continue;
            }

            if (TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                || NamedFiles.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase))
            {
                // Verified in this direction too. Round 11 caught "declared binary, actually text";
                // this is its mirror, and it is the only route past all three capture layers, because
                // every one of them keys on the name — the .gitignore patterns, Extensions() against
                // NeverCommitted, and the unclassified-fails default. A capture renamed to .md is
                // declared text, swept as text, and its content read as prose that happens to match
                // nothing. Content is the one thing a rename cannot change.
                if (HasNulByte(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))
                {
                    mislabelled.Add($"{rel}  (declared text, contains NUL)");
                }

                continue;
            }

            if (BinaryExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                // Verify the declaration rather than trusting it. `.svg` sat on the binary list while
                // being UTF-8 text with human prose comments in it, and the guard endorsed that: closing
                // the "on no list" hole did nothing about the "on the wrong list" one, and a wrong
                // declaration satisfied this test exactly as well as a right one. A real binary has a NUL
                // in its first few KB; a text file claiming to be binary is a file nothing reads.
                if (File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)))
                    && !HasNulByte(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))
                {
                    mislabelled.Add($"{rel}  (declared binary, no NUL)");
                }

                continue;
            }

            unclassified.Add(rel);
        }

        Assert.True(
            forbidden.Count == 0,
            "Committed files of a type that must never be committed. These hold raw session material by "
            + "definition - tokens, account identifiers, device ids, console names, addresses - and there "
            + "is no benign reason for one to be in the tree. Remove it, then purge it from history while "
            + "that is still cheap, and check why .gitignore did not stop it:\n  "
            + string.Join("\n  ", forbidden));

        Assert.True(
            mislabelled.Count == 0,
            "Files whose declared kind does not match their content. Declared binary but text means a "
            + "file nothing sweeps; declared text but binary means a renamed blob being read as prose, "
            + "which is the one route past every capture rule here since all of them key on the name. "
            + "Move the extension, or find out why the content is not what the name says:\n  "
            + string.Join("\n  ", mislabelled));

        Assert.True(
            unclassified.Count == 0,
            "Committed files that no sweep opens and that are not declared binary. Add the extension to "
            + "TextExtensions (or the name to NamedFiles) if it is text, or to BinaryExtensions if it is "
            + "not. Leaving it unclassified means it is neither swept nor knowingly skipped, which is how "
            + "fourteen product-code files stayed unread for eight rounds:\n  "
            + string.Join("\n  ", unclassified));
    }

    /// <summary>CRLF to LF, so a hash describes content rather than whichever checkout produced it.</summary>
    private static byte[] Normalised(byte[] bytes)
    {
        List<byte> outBytes = new(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0d && i + 1 < bytes.Length && bytes[i + 1] == 0x0a) continue;
            outBytes.Add(bytes[i]);
        }

        return [.. outBytes];
    }

    /// <summary>A file is binary if its first 8 KB contain a NUL. Cheap, and content cannot be renamed.</summary>
    private static bool HasNulByte(string path)
    {
        if (!File.Exists(path)) return false;

        byte[] head = new byte[Math.Min(8192, (int)new FileInfo(path).Length)];
        using FileStream fs = File.OpenRead(path);
        fs.ReadExactly(head);
        return Array.IndexOf(head, (byte)0) >= 0;
    }

    /// <summary>
    /// The third corpus: not the tree, not the messages, but every revision of every file a clone carries.
    /// See <see cref="HistoricalBlobOffenders"/> for the redaction this one caught too late.
    /// </summary>
    [Fact]
    public void HistoricalBlobs_CarryNothingUnredacted()
    {
        (bool readable, string? reason) = MessageCorpusState();
        Assert.True(reason is null, reason);
        if (!readable) return;

        List<string> offenders = HistoricalBlobOffenders();
        Assert.True(
            offenders.Count == 0,
            "Unredacted values in historical blobs. Redacting the file is not enough — the revision before "
            + "the redaction still ships with every clone. While this repository is unpublished a "
            + "`git filter-repo --replace-text` pass is cheap and total; once it is public it is neither. "
            + "Allowlist the value with its reason only if it is genuinely benign:\n  "
            + string.Join("\n  ", offenders.Take(40))
            + (offenders.Count > 40 ? $"\n  ... and {offenders.Count - 40} more" : string.Empty));
    }

    /// <summary>
    /// The other published corpus. See <see cref="CommitMessageOffenders"/> for why it is not a file, and
    /// why that mattered.
    /// </summary>
    [Fact]
    public void CommitMessages_CarryNoUnredactedValues()
    {
        (bool readable, string? reason) = MessageCorpusState();
        Assert.True(reason is null, reason);
        if (!readable) return;   // no git, or no .git: an honest absence, not a truncated corpus

        List<string> offenders = CommitMessageOffenders();
        Assert.True(
            offenders.Count == 0,
            "Unredacted values in commit messages. If the commit is not yet pushed, `git commit --amend` is "
            + "the whole fix and it is worth doing now - afterwards this needs a history rewrite. Describe "
            + "the class of value instead of repeating it, the way the docs do:" + (char)10 + "  "
            + string.Join((char)10 + "  ", offenders));
    }

    /// <summary>
    /// The belief under test: each row is a gap that was once real, or a value that must not cry wolf.
    ///
    /// <para>This runs the value through <see cref="ScanLine"/> — the same function the two file-sweeping
    /// facts use — placed on a line of the shape its context names, so the answer is what the sweep would
    /// actually do rather than what a regex does in isolation. The allowlist is off: this asks what the
    /// detector can <em>see</em>, and what is deliberately tolerated afterwards is
    /// <see cref="EveryAllowlistEntry_SuppressesSomethingReal"/>'s question, not this one. (With it on, the
    /// dash-MAC row would be suppressed by the synthetic-fixture entry and prove nothing.)</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(DetectorContract))]
    public void Detector_HonoursItsContract(string input, LineContext context, bool shouldMatch, string why)
    {
        string line = context switch
        {
            LineContext.Prose => input,
            LineContext.DeclaredConstant or LineContext.ProductCode =>
                $"    private const string Fixture = \"{input}\";",
            LineContext.XamlIdentifier => $"                    x:Uid=\"{input}\" />",
            LineContext.Comment => $"        StartSession();   // as captured: {input}",
            LineContext.Code => $"        var fixture = Decode(\"{input}\");",
            _ => throw new ArgumentOutOfRangeException(nameof(context)),
        };

        List<string> hits = ScanLine(
            line,
            isProse: context == LineContext.Prose,
            applyAllowlist: false,
            at: "contract",
            isProductCode: context == LineContext.ProductCode);

        Assert.True(
            hits.Count > 0 == shouldMatch,
            shouldMatch
                ? $"the sweep must catch \"{input}\" in {context} — {why}"
                : $"the sweep must tolerate \"{input}\" in {context} — {why}\n  reported: "
                  + string.Join(", ", hits));
    }

    /// <summary>
    /// The corpus named "committed text" contains only committed text.
    ///
    /// <para>Stated as a test rather than left to the implementation because the two ways of being wrong
    /// have opposite signs and only one of them is loud. Reading untracked files produces false findings,
    /// which is what actually happened and is at least visible. Reading <em>fewer</em> files than git
    /// tracks would make this whole file pass while checking less than it claims, and nothing else here
    /// would notice — <see cref="EveryCommittedTextFile_IsSwept"/> checks classification coverage, not
    /// that the sweep opened anything.</para>
    /// </summary>
    [Fact]
    public void SweptCorpus_ContainsOnlyTrackedFiles()
    {
        string root = RepoRoot();
        string? listing = RunGit(root, "ls-files");
        if (listing is null) return;   // no git: the fallback walk is deliberate, and this cannot judge it

        HashSet<string> tracked = new(
            listing.Split((char)0x0a).Select(r => r.TrimEnd((char)0x0d)).Where(r => r.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        List<string> untracked = CommittedText()
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(rel => !tracked.Contains(rel))
            .ToList();

        Assert.True(
            untracked.Count == 0,
            "The sweep opened files git does not track. A finding in one of these is not a finding — the "
            + "file cannot be published — and noise here is what gets a real finding skimmed past:\n  "
            + string.Join("\n  ", untracked.Take(20)));

        // And the corpus is not empty, because an empty one passes everything.
        Assert.True(CommittedText().Any(), "the swept corpus is empty, so every assertion in this file is vacuous");
    }

    /// <summary>
    /// Which paths get the strict scope. <see cref="IsProductCode"/> decides which of two hex rules applies
    /// and whether <see cref="CodeBase64Literal"/> runs at all, so a path drifting out of it weakens the
    /// detector everywhere in that directory at once — and does it silently, which is how the extraction of
    /// <c>ports/common/</c> demoted the whole portable core in a refactor that touched no test.
    ///
    /// <para>The rows that matter are the last two. A directory layout nobody has invented yet must land on
    /// the strict side by default, because the alternative is a guard that quietly stops covering new code.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("src/Ripcord.Protocol.Halyard.Common/Crypto/V1/HalyardV1SessionCrypto.cs", true)]
    [InlineData("tools/Ripcord.ProtocolLab/Program.cs", true)]
    [InlineData("tests/Ripcord.Protocol.Halyard.Tests/LiveControlVectorTests.cs", false)]
    [InlineData("ports/ripcord-3ds/source/util/rc_random.c", true)]
    [InlineData("ports/ripcord-ps3/source/media/rc_h264_bits.c", true)]
    [InlineData("ports/ripcord-ps3/tests/h264_test.c", false)]
    [InlineData("ports/common/crypto/rc_aes.c", true)]
    [InlineData("ports/common/util/rc_base64.c", true)]
    [InlineData("ports/common/tests/fec_test.c", false)]
    [InlineData("ports/a-port-that-does-not-exist-yet/media/decoder.c", true)]
    [InlineData("ports/a-port-that-does-not-exist-yet/tests/decoder_test.c", false)]
    public void ProductCodeClassification(string relative, bool expected)
        => Assert.Equal(expected, IsProductCode(relative));

    /// <summary>
    /// A committed constant the prose-only scope cannot see, checked by the hash of its value. The two data
    /// files beside it are pinned the same way; this closes the last declared constant that had no
    /// value-level guard at all.
    /// </summary>
    [Fact]
    public void PinnedConstants_HaveNotChanged()
    {
        List<string> drifted = [];

        foreach ((string name, (string expected, Func<string> read)) in PinnedConstants)
        {
            string actual = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(read())))
                .ToLowerInvariant();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                drifted.Add($"{name}\n      expected {expected}\n      actual   {actual}");
            }
        }

        Assert.True(
            drifted.Count == 0,
            "A pinned constant changed. It is committed under a bounded exception, so confirm against "
            + "CLAUDE.md's inventory that the new value is still generic — identical for every console and "
            + "every account — and amend NOTICE with it before re-pinning:\n    "
            + string.Join("\n    ", drifted));
    }

    /// <summary>
    /// An allowlist entry matching nothing asserts a coverage that does not exist. That has happened twice —
    /// once because the shipped regex could not reach the text the entry was written for, once because two
    /// new scope rules quietly put the text out of range — and both times an auditor found it rather than CI.
    /// </summary>
    [Fact]
    public void EveryAllowlistEntry_SuppressesSomethingReal()
    {
        HashSet<string> Values(IEnumerable<string> offenders) =>
            new(offenders.Select(o => Normalise(o.Split('|')[^1])), StringComparer.OrdinalIgnoreCase);

        HashSet<string> fromFiles = Values(Offenders(applyAllowlist: false));

        // Each dictionary is asserted against the corpus it silences, and only that one. A shallow clone is
        // NOT a legitimate absence and must not take this exit — it is a truncated corpus pretending to be
        // a whole one; MessageCorpusState reports it and the assertion below leads with that.
        (bool readable, string? corpusProblem) = MessageCorpusState();
        HashSet<string> fromMessages =
            readable ? Values(CommitMessageOffenders(false)) : new(StringComparer.OrdinalIgnoreCase);

        // Exact, because exact is what IsAllowed does. A substring test would call an entry live when its
        // text merely occurs inside a longer run the detector reports whole — suppressing nothing, which is
        // the condition this test exists to find. Validating against a looser predicate than the one that
        // ships is this file's signature defect; it had crept into the test written to prevent it.
        List<string> inert = Allowed
            .Where(e => !fromFiles.Contains(Normalise(e.Key)))
            .Select(e => $"{e.Key}  (\"{e.Value}\")  [file corpus]")
            .Concat(readable
                ? AllowedInMessages
                    .Where(e => !fromMessages.Contains(Normalise(e.Key)))
                    .Select(e => $"{e.Key}  (\"{e.Value}\")  [message corpus]")
                : [])
            .ToList();

        Assert.True(
            inert.Count == 0,
            (corpusProblem is null
                ? string.Empty
                : "READ THIS FIRST — the entries below are probably fine and the corpus is not: "
                  + corpusProblem + "\n\n")
            + "Allowlist entries that suppress nothing. Each states a reason for tolerating a value the detector "
            + "never produces, which asserts a coverage that does not exist. Either the value is gone (delete "
            + "the entry) or the detector cannot reach it (fix the detector):\n  "
            + string.Join("\n  ", inert));
    }
}
