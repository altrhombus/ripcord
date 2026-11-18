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
/// The pairing layer reports each stage to a <c>Log</c> sink: push connected, session created, connect
/// command sent, console joined, seed recovered, OFFER received. This is that sink for the app, so a failed
/// pairing can tell "the console never joined" from "it joined and published nothing" from "it published
/// something we could not read".
/// </para>
///
/// <para>
/// <b>Off unless asked for:</b> <c>RIPCORD_TRACE_PAIRING=1</c>. It is a record of one account reaching one
/// console, and that is not something to leave accumulating on disk without being told to.
/// </para>
///
/// <para>
/// <b>Identifiers are replaced by their shape.</b> Session ids, device ids and hashed ids go through as
/// <c>&lt;id:32&gt;</c> — the length and nothing else. A trace exists to be handed to somebody, and this
/// project's rule is that nothing tied to a specific console or account leaves the captures folder. The
/// length is kept because a wrong-length id is a real fault.
/// </para>
/// </summary>
public sealed class HalyardPairingTrace
{
    /// <summary>
    /// A GUID, a long hex run, or a long base64 run. Anything id-shaped, not any particular field.
    ///
    /// <para>
    /// <b>The base64 branch requires a digit or a <c>+</c>/<c>/</c>,</b> which is not fussiness: without
    /// it the run of letters in a long camelCase field name is itself a valid base64 run, and a trace came
    /// back reading <c>&lt;id:23&gt;=[networkStandby,mainOnStandby]</c> — the redactor had eaten
    /// <c>wakeupEnabledPowerModes</c>, which is the NAME of the field and not its value. Base64 of real
    /// key material effectively always carries a digit; an identifier a programmer typed does not.
    /// </para>
    /// </summary>
    private static readonly Regex Identifier = new(
        @"\b([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
        + @"|[0-9a-fA-F]{16,}"
        + @"|(?=[A-Za-z0-9+/]*[0-9+/])[A-Za-z0-9+/]{20,}={0,2})\b",
        RegexOptions.Compiled);

    private readonly string _path;
    private readonly object _gate = new();

    private DateTimeOffset _previous;

    private HalyardPairingTrace(string path)
    {
        _path = path;
        _previous = DateTimeOffset.Now;

        Write($"==== pairing trace opened {DateTimeOffset.Now:O} (at app start, not at a pairing) ====");
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

    /// <summary>
    /// Wall clock, plus the gap since the previous line.
    ///
    /// <para>
    /// <b>Not elapsed-since-start.</b> The sink is built with the service graph, at app start, so an elapsed
    /// clock counts from launch: the first line of a pairing read "44690 ms", which looks like a 44-second
    /// stall connecting the push channel and was the user walking through the UI to the Pair button. Every
    /// wait in the rendezvous is a gap between two lines, so each line carries that gap.
    /// </para>
    /// </summary>
    private void Write(string line)
    {
        try
        {
            string safe = Identifier.Replace(line, m => $"<id:{m.Length}>");

            // The sink is handed to a layer that calls it from the push receive loop as well as from the
            // pairing task, so two lines can genuinely race. The one lock here is for the file and the
            // previous-timestamp pair, which have to move together or the gaps stop adding up.
            lock (_gate)
            {
                DateTimeOffset now = DateTimeOffset.Now;
                TimeSpan gap = now - _previous;
                _previous = now;

                File.AppendAllText(
                    _path,
                    $"{now:HH:mm:ss.fff}  +{gap.TotalSeconds,6:0.000}s  {safe}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Same rule as the crash log and the focus trace: a diagnostic never takes the app down.
        }
    }
}
