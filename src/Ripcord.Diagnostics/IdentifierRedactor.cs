using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ripcord.Diagnostics;

/// <summary>
/// Replaces the values in a line of output that identify a network, a device or an account with the
/// placeholders <c>docs/README.md</c> lists, so what a harness prints is already safe to paste into a record.
///
/// <para>
/// <b>Why at the source.</b> A hardware run is written up from what the harness printed, and a record copied
/// from raw output carries whatever the output carried: a console's name, its address, its id. Every leak this
/// project has had to clean up arrived that way. Redacting as the line is written means the safe form is the
/// only one there is to copy, and the raw one is a deliberate request (the harness's
/// <c>--show-identifiers</c>).
/// </para>
///
/// <para>
/// <b>What it recognises.</b> By shape: IPv4 addresses, dotted or in the Host header's padded columns, except
/// the loopback, unspecified, broadcast, multicast, documentation and synthetic-LAN ones, which identify
/// nothing; 19-digit numbers, an account id's shape; and hex of 12 or more digits with a letter or a
/// separator in it, which is every MAC, host id, device id and key. By value: the names and addresses it has
/// been taught, and any extra patterns its owner adds. A value it has not been taught and whose shape is
/// ordinary, such as a console renamed to a plain word it has never seen, passes. That is why the harness
/// teaches it every name it meets.
/// </para>
///
/// <para>Thread-safe: harness output arrives from session threads as well as the main one.</para>
/// </summary>
public sealed class IdentifierRedactor
{
    private static readonly Regex Address = new(
        @"(?<![\d.])(\d{1,3})\. {0,2}(\d{1,3})\. {0,2}(\d{1,3})\. {0,2}(\d{1,3})(?![\d.])", RegexOptions.Compiled);

    private static readonly Regex AccountShaped = new(@"(?<!\d)\d{19}(?!\d)", RegexOptions.Compiled);

    // Twelve or more hex digits, a separator allowed between any two, with an optional 0x: MACs written with
    // colons or dashes, bare host ids, keys and device ids of any length, odd ones included.
    private static readonly Regex HexRun = new(
        @"(?<![0-9A-Za-z])(?:0[xX])?[0-9A-Fa-f](?:[:-]?[0-9A-Fa-f]){11,}(?![0-9A-Za-z])", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _consoleAddresses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Regex Pattern, string Replacement)> _patterns = [];
    private Regex? _learned;

    /// <summary>Teaches it a name that identifies real hardware; it becomes <c>&lt;hostname&gt;</c>.</summary>
    public void LearnName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2) return;
        lock (_gate)
        {
            if (_names.Add(name.Trim())) _learned = null;
        }
    }

    /// <summary>Teaches it a console's address, so it reads <c>&lt;console-ip&gt;</c> rather than the client's.</summary>
    public void LearnConsoleAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        lock (_gate) _consoleAddresses.Add(address.Trim());
    }

    /// <summary>A value that must never appear, whatever its shape; it becomes <c>&lt;redacted&gt;</c>.</summary>
    public void Deny(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 3) return;
        lock (_gate)
        {
            if (_denied.Add(value.Trim())) _learned = null;
        }
    }

    /// <summary>An extra pattern and its replacement, applied before the built-in rules.</summary>
    public void AddPattern(Regex pattern, string replacement)
    {
        lock (_gate) _patterns.Add((pattern, replacement));
    }

    public string Redact(string? line)
    {
        if (string.IsNullOrEmpty(line)) return line ?? string.Empty;

        Regex? learned;
        List<(Regex, string)> patterns;
        HashSet<string> consoles;
        HashSet<string> denied;
        lock (_gate)
        {
            learned = _learned ??= BuildLearned();
            patterns = [.. _patterns];
            consoles = [.. _consoleAddresses];
            denied = new HashSet<string>(_denied, StringComparer.OrdinalIgnoreCase);
        }

        string result = line;
        if (learned is not null)
        {
            result = learned.Replace(result, m => denied.Contains(m.Value) ? "<redacted>" : "<hostname>");
        }

        foreach ((Regex pattern, string replacement) in patterns)
        {
            result = pattern.Replace(result, replacement);
        }

        result = Address.Replace(result, m =>
        {
            byte[]? octets = Octets(m);
            if (octets is null || Benign(octets)) return m.Value;
            string dotted = string.Join('.', octets);
            return consoles.Contains(dotted) ? "<console-ip>" : "<client-ip>";
        });
        result = AccountShaped.Replace(result, "<redacted>");
        result = HexRun.Replace(result, m => m.Value.Any(c => char.IsAsciiLetter(c) || c is ':' or '-')
            ? "<redacted>"
            : m.Value);
        return result;
    }

    private Regex? BuildLearned()
    {
        IEnumerable<string> all = _names.Concat(_denied).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(s => s.Length);
        string alternation = string.Join('|', all.Select(Regex.Escape));
        return alternation.Length == 0
            ? null
            : new Regex(@"(?<![\w.-])(?:" + alternation + @")(?![\w-])", RegexOptions.IgnoreCase);
    }

    private static byte[]? Octets(Match m)
    {
        var octets = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (!byte.TryParse(m.Groups[i + 1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out octets[i]))
                return null;
        }

        return octets;
    }

    /// <summary>
    /// Addresses that identify nothing: loopback, unspecified, broadcast, multicast, the RFC 5737
    /// documentation ranges, and this project's synthetic LANs (docs/README.md).
    /// </summary>
    private static bool Benign(byte[] o) => o[0] switch
    {
        127 or 0 => true,
        255 => o[1] == 255 && o[2] == 255 && o[3] == 255,
        >= 224 and <= 239 => true,
        10 => o[1] == 0 && o[2] == 0,
        172 => o[1] == 31 && o[2] == 0,
        192 => (o[1] == 168 && o[2] == 1) || (o[1] == 0 && o[2] == 2),
        198 => o[1] == 51 && o[2] == 100,
        203 => o[1] == 0 && o[2] == 113,
        _ => false,
    };
}

/// <summary>
/// A <see cref="TextWriter"/> that passes each complete line through an <see cref="IdentifierRedactor"/>
/// before the inner writer sees it. Installed over <see cref="Console.Out"/> and <see cref="Console.Error"/>,
/// it covers every line a harness and the libraries under it print, without touching the call sites.
/// </summary>
public sealed class RedactingTextWriter(TextWriter inner, IdentifierRedactor redactor) : TextWriter
{
    private readonly StringBuilder _line = new();
    private readonly object _gate = new();

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                FlushLine(newline: true);
            }
            else
            {
                _line.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        lock (_gate)
        {
            int start = 0;
            for (int i = value.IndexOf('\n'); i >= 0; i = value.IndexOf('\n', start))
            {
                _line.Append(value, start, i - start);
                FlushLine(newline: true);
                start = i + 1;
            }

            _line.Append(value, start, value.Length - start);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            Write(value);
            FlushLine(newline: true);
        }
    }

    public override void Flush()
    {
        lock (_gate)
        {
            // A prompt written without a newline has to reach the terminal before the program waits for input.
            FlushLine(newline: false);
            inner.Flush();
        }
    }

    private void FlushLine(bool newline)
    {
        string text = _line.ToString().TrimEnd('\r');
        bool carriage = _line.Length > 0 && _line[^1] == '\r';
        _line.Clear();
        if (text.Length > 0) inner.Write(redactor.Redact(text));
        if (newline) inner.Write(carriage ? "\r\n" : "\n");
        if (newline) inner.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Flush();
        base.Dispose(disposing);
    }
}
