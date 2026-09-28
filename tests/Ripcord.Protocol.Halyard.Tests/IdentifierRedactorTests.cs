using System.Text.RegularExpressions;
using Ripcord.Diagnostics;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// <see cref="IdentifierRedactor"/>: what a harness prints must already be safe to paste into a record.
///
/// <para>The identifying inputs are assembled at runtime rather than written down, so this file holds no value
/// shaped like a real address, key or account id, and the published-tree sweep has nothing to allowlist.</para>
/// </summary>
public class IdentifierRedactorTests
{
    private static string Dotted(params int[] o) => string.Join('.', o);
    private static string Padded(params int[] o) => string.Join('.', o.Select(x => x.ToString().PadLeft(3)));
    private static string Hex(int start, int count) => string.Concat(Enumerable.Range(start, count).Select(i => (i & 0xff).ToString("x2")));

    [Fact]
    public void AnAddressIsRedactedInEitherForm_AndTheConsoleIsToldApartFromTheClient()
    {
        var r = new IdentifierRedactor();
        string console = Dotted(172, 20, 0, 5), client = Dotted(172, 20, 0, 9);
        r.LearnConsoleAddress(console);

        Assert.Equal("registering with <console-ip> from <client-ip>", r.Redact($"registering with {console} from {client}"));
        Assert.Equal("Host: <console-ip>:9295", r.Redact($"Host: {Padded(172, 20, 0, 5)}:9295"));
        Assert.Equal("peer <client-ip>", r.Redact($"peer {Dotted(99, 1, 2, 3)}"));
    }

    [Fact]
    public void AddressesThatIdentifyNothingArePrintedAsTheyAre()
    {
        var r = new IdentifierRedactor();
        string line = "127.0.0.1 0.0.0.0 255.255.255.255 224.0.0.251 10.0.0.7 172.31.0.1 192.168.1.20 "
                      + "192.0.2.104 198.51.100.1 203.0.113.9 Host: 192.  0.  2.104:9295 version 10.0.26100.0";
        Assert.Equal(line, r.Redact(line));
    }

    [Fact]
    public void KeysIdsAndMacsAreRedactedWhateverTheirSeparators()
    {
        var r = new IdentifierRedactor();
        string key = Hex(0x10, 16), mac = Hex(0xa0, 6);
        string colons = string.Join(':', Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));

        Assert.Equal("key <redacted>", r.Redact($"key {key}"));
        Assert.Equal("key <redacted>", r.Redact($"key 0x{key.ToUpperInvariant()}"));
        Assert.Equal("key <redacted>", r.Redact($"key {key}f"));   // an odd length
        Assert.Equal("mac <redacted> and <redacted>", r.Redact($"mac {colons} and {mac}"));

        // The trade, stated: twelve or more DECIMAL digits with no separator pass, because that is a count or a
        // timestamp far more often than an id. A real MAC is all-decimal about 0.35% of the time.
        Assert.Equal("202122232425", r.Redact("202122232425"));
    }

    [Fact]
    public void AnAccountIdIsRedacted_ButOrdinaryNumbersAreNot()
    {
        var r = new IdentifierRedactor();
        string account = (1_000_000_000_000_000_000UL + 4242UL).ToString();
        Assert.Equal("account <redacted>", r.Redact($"account {account}"));
        Assert.Equal("5749 packets, 1727553600000 ms, 1920x1080 at 60", r.Redact("5749 packets, 1727553600000 ms, 1920x1080 at 60"));
    }

    [Fact]
    public void LearnedNamesDeniedValuesAndPatternsAreRedacted()
    {
        var r = new IdentifierRedactor();
        r.LearnName("Den Box");
        r.Deny("hunter-ssid");
        r.AddPattern(new Regex(@"\bPS[45]-\d{3}\b"), "<hostname>");

        Assert.Equal("found <hostname> and <hostname>", r.Redact("found den box and PS5-" + "123"));
        Assert.Equal("wifi <redacted>", r.Redact("wifi Hunter-SSID"));
        Assert.Equal("the den boxer", r.Redact("the den boxer"));   // a name inside a longer word is not the name
    }

    [Fact]
    public void TheWriterRedactsWholeLines_HoweverTheyAreWritten()
    {
        var r = new IdentifierRedactor();
        var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, r);
        string key = Hex(0x40, 16);

        writer.Write("key " + key.Substring(0, 10));   // a value split across writes is still one value
        writer.Write(key.Substring(10) + "\nnext ");
        writer.WriteLine("line");
        writer.Write("prompt> ");
        writer.Flush();

        Assert.Equal("key <redacted>\nnext line" + Environment.NewLine.Replace("\r\n", "\n") + "prompt> ",
            inner.ToString().Replace("\r\n", "\n"));
    }
}
