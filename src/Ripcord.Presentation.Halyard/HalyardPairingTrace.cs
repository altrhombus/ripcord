using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Ripcord.Core.Platform;

namespace Ripcord.Presentation.Halyard;

/// <summary>
/// Writes the account-pairing rendezvous down, stage by stage, so a failure says where it stopped.
///
/// <para>
/// <b>Why.</b> The rendezvous already narrates itself — the pairing layer takes a <c>Log</c> sink and reports
/// push connected, session created, connect command sent, console joined, seed recovered, OFFER received. The
/// console harness wires it and prints the lines; the app passed null, so an account pairing that failed in
/// the app produced one sentence about the last thing it was waiting for and nothing about how far it got.
/// That is not enough to tell "the console never joined" from "it joined and published nothing" from "it
/// published something we could not read", and those three have nothing in common but the symptom.
/// </para>
///
/// <para>
/// <b>Off unless asked for:</b> <c>RIPCORD_TRACE_PAIRING=1</c>. Not because it is expensive — it is a handful
/// of lines — but because it is a record of one account reaching one console, and that is not something to
/// leave accumulating on disk without being told to.
/// </para>
///
/// <para>
/// <b>Identifiers are replaced by their shape.</b> Session ids, device ids and hashed ids go through as
/// <c>&lt;id:32&gt;</c> — the length and nothing else. A trace exists to be handed to somebody, very possibly
/// pasted into a chat, and this project's rule is that nothing tied to a specific console or account leaves
/// the dirty room. The length is kept because a wrong-length id is a real fault and the shape is what tells
/// you so.
/// </para>
/// </summary>
public sealed class HalyardPairingTrace
{
    /// <summary>A GUID, a long hex run, or a long base64 run. Anything id-shaped, not any particular field.</summary>
    private static readonly Regex Identifier = new(
        @"\b([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
        + @"|[0-9a-fA-F]{16,}"
        + @"|[A-Za-z0-9+/]{20,}={0,2})\b",
        RegexOptions.Compiled);

    private readonly string _path;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private HalyardPairingTrace(string path)
    {
        _path = path;
        Write($"==== account pairing — {DateTimeOffset.Now:O} ====");
    }

    /// <summary>True when <c>RIPCORD_TRACE_PAIRING</c> asks for tracing.</summary>
    public static bool Enabled
        => Environment.GetEnvironmentVariable("RIPCORD_TRACE_PAIRING") is "1" or "true" or "TRUE";

    /// <summary>
    /// A sink to hand the pairing options, or null when tracing is off — which is also what the options field
    /// means by "no log", so the caller needs no branch.
    /// </summary>
    public static Action<string>? SinkIfEnabled(IPlatformPaths paths)
    {
        if (!Enabled)
        {
            return null;
        }

        try
        {
            var trace = new HalyardPairingTrace(Path.Combine(paths.StateDirectory, "pairing-trace.log"));
            return trace.Write;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Ripcord] pairing trace could not start: {ex}");
            return null;
        }
    }

    private void Write(string line)
    {
        try
        {
            string safe = Identifier.Replace(line, m => $"<id:{m.Length}>");
            File.AppendAllText(_path, $"[{_clock.ElapsedMilliseconds,7} ms] {safe}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception)
        {
            // Same rule as the crash log and the focus trace: a diagnostic never takes the app down.
        }
    }
}
