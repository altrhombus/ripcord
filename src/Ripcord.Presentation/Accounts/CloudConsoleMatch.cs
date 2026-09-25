using Ripcord.Core.Consoles;

namespace Ripcord.Presentation.Accounts;

/// <summary>
/// Matching a console we have paired against the same console as the account service reports it.
///
/// <para>
/// Two callers need this and must agree: pairing, which learns the cloud id at the moment it saves a console,
/// and the backfill that repairs consoles paired before sign-in existed. They agreed by coincidence when the
/// rule lived inside the pairing flow, which is the kind of agreement that stops being true the first time one
/// of them is edited.
/// </para>
/// </summary>
public static class CloudConsoleMatch
{
    /// <summary>
    /// The account service's id for a console, matched by the name the console reports for itself.
    ///
    /// <para>
    /// <b>Name is the only thing the two sources share</b> — the cloud list carries no address, the local scan
    /// carries no account id — and it is the same string on both sides, because the console reports one name
    /// for itself.
    /// </para>
    ///
    /// <para>
    /// <b>An ambiguous match yields nothing rather than a guess.</b> Two consoles genuinely called the same
    /// thing is exactly why nicknames exist, and a wrong id here would send a wake to someone else's console.
    /// </para>
    /// </summary>
    /// <param name="reportedName">
    /// What the console calls <em>itself</em>, never the user's nickname. Passing a display name would fail to
    /// match every console the user has renamed — which is most of the ones they care about, since renaming is
    /// what people do to tell two consoles apart.
    /// </param>
    public static string? ResolveId(IReadOnlyList<CloudConsole> cloudConsoles, string? reportedName)
        => Resolve(cloudConsoles, reportedName)?.Id;

    /// <summary>
    /// The account service's whole record for a console, matched the same way — and by the same code, so the
    /// ambiguity rule cannot diverge between the caller that wants an id and the caller that wants the flags.
    ///
    /// <para>
    /// The record carries more than the id: whether remote play is enabled and whether the console permits being
    /// woken. A caller deciding <em>whether to offer</em> the account route needs those, and asking for the id
    /// and then looking the console up again would be a second match with its own chance of disagreeing.
    /// </para>
    /// </summary>
    public static CloudConsole? Resolve(IReadOnlyList<CloudConsole> cloudConsoles, string? reportedName)
    {
        ArgumentNullException.ThrowIfNull(cloudConsoles);

        if (string.IsNullOrWhiteSpace(reportedName) || cloudConsoles.Count == 0)
        {
            return null;
        }

        CloudConsole? found = null;
        foreach (CloudConsole candidate in cloudConsoles)
        {
            if (!string.Equals(candidate.Name, reportedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is not null)
            {
                return null; // ambiguous
            }

            found = candidate;
        }

        return found;
    }

    /// <summary>
    /// Fill in the cloud id on stored consoles that do not have one, and report how many were repaired.
    ///
    /// <para>
    /// This exists because the field was added after people had already paired their consoles. Without it,
    /// remote wake would work only for consoles paired from the day the account tier shipped, and there would
    /// be nothing on screen to explain why an older console behaved differently — the wake would simply never
    /// be attempted.
    /// </para>
    ///
    /// <para>
    /// Only ever fills a blank. An id already stored is never overwritten, so a console that has been re-paired
    /// or hand-corrected keeps what it has, and running this repeatedly is a no-op after the first time.
    /// </para>
    /// </summary>
    public static int Backfill(IPairedConsoleStore store, IReadOnlyList<CloudConsole> cloudConsoles)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cloudConsoles);

        if (cloudConsoles.Count == 0)
        {
            return 0;
        }

        int repaired = 0;
        foreach (PairedConsole console in store.Load())
        {
            if (!string.IsNullOrWhiteSpace(console.CloudDeviceId))
            {
                continue;
            }

            // ReportedName is what the console broadcast about itself at pairing time. Falling back to Name
            // covers records written before that was captured; those hold the family label ("PlayStation 5"),
            // which will simply not match anything, and not matching is the correct outcome there.
            string? id = ResolveId(cloudConsoles, console.ReportedName ?? console.Name);
            if (id is null)
            {
                continue;
            }

            store.Upsert(console with { CloudDeviceId = id });
            repaired++;
        }

        return repaired;
    }
}
