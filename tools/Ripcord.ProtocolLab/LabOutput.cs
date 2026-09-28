using System.Text.RegularExpressions;
using Ripcord.Diagnostics;

namespace Ripcord.ProtocolLab;

/// <summary>
/// Everything the lab prints goes through an <see cref="IdentifierRedactor"/>, so a hardware run's output is
/// already safe to paste into the journal: console names, addresses, account ids, MACs and keys come out as the
/// placeholders <c>docs/README.md</c> lists. <c>--show-identifiers</c> prints them raw, for debugging on your
/// own machine. The two sign-in URLs are raw either way, since they are for opening, not for a record.
/// </summary>
internal static class LabOutput
{
    public static IdentifierRedactor Redactor { get; } = new();

    /// <summary>The terminal itself, for output that must not be altered.</summary>
    public static TextWriter Raw { get; private set; } = Console.Out;

    /// <summary>Removes <c>--show-identifiers</c> from <paramref name="args"/> and, unless it was there, redacts.</summary>
    public static string[] Install(string[] args)
    {
        bool show = args.Contains("--show-identifiers", StringComparer.Ordinal);
        string[] rest = [.. args.Where(a => a != "--show-identifiers")];
        Raw = Console.Out;
        if (show) return rest;

        Redactor.AddPattern(new Regex(@"\bPS[45]-\d{3}\b"), "<hostname>");
        // An address on the command line is always the console's: register, connect, wake and the rest take it.
        foreach (string arg in rest)
        {
            if (System.Net.IPAddress.TryParse(arg, out _)) Redactor.LearnConsoleAddress(arg);
        }

        LoadDenylist();
        Console.SetOut(new RedactingTextWriter(Console.Out, Redactor));
        Console.SetError(new RedactingTextWriter(Console.Error, Redactor));
        return rest;
    }

    /// <summary>
    /// On the owner's machine the leak guard's denylist (tools/leak-guard) names the real values, so the lab
    /// redacts those too. The shape rules already cover every hex, account-id and address entry, so only the
    /// words are read: names, SSIDs, the online id, emails and the account id's base64 forms.
    /// </summary>
    private static void LoadDenylist()
    {
        string? path = FindDenylist();
        if (path is null) return;
        foreach (string line in File.ReadLines(path))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 2 || line.StartsWith('#')) continue;
            switch (parts[0])
            {
                case "name":
                    Redactor.LearnName(parts[1]);
                    break;
                case "ssid" or "online-id" or "email":
                    Redactor.Deny(parts[1]);
                    break;
                case "account-id" when !parts[1].All(char.IsAsciiHexDigit):
                    Redactor.Deny(parts[1]);
                    break;
            }
        }
    }

    private static string? FindDenylist()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "docs", "protocol", "captures", "leak-denylist.tsv");
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }
}
