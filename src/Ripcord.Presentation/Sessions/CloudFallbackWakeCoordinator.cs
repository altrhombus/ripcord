using Ripcord.Core.Consoles;
using Ripcord.Presentation.Accounts;

namespace Ripcord.Presentation.Sessions;

/// <summary>
/// Adds a remote wake to whatever local wake the platform already provides.
///
/// <para>
/// <b>Why a decorator rather than a branch inside the platform coordinator.</b> The two mechanisms have nothing
/// in common: one is a signed broadcast on the console's own subnet, the other is a REST call to an account
/// service that reaches the console through PSN's server-side fan-out. Keeping them separate means the local
/// path stays exactly as it was — it needs no account, no network beyond the LAN, and no credential — and this
/// type holds the entire "and if that didn't work, try the cloud" policy in one readable place.
/// </para>
///
/// <para>
/// <b>Local first, always.</b> The local wake is faster, needs nothing from the internet, and can actually
/// confirm the console came up by probing it. The remote one is a fallback for the case the local one cannot
/// address at all: a console that is not on this network, where the broadcast never reaches it.
/// </para>
/// </summary>
public sealed class CloudFallbackWakeCoordinator(
    IConsoleWakeCoordinator local,
    IAccountSession account) : IConsoleWakeCoordinator
{
    private readonly IConsoleWakeCoordinator _local = local ?? throw new ArgumentNullException(nameof(local));
    private readonly IAccountSession _account = account ?? throw new ArgumentNullException(nameof(account));

    public async Task<ConsoleWakeOutcome> EnsureAwakeAsync(
        PairedConsole console,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);

        ConsoleWakeOutcome local = await _local
            .EnsureAwakeAsync(console, progress, cancellationToken)
            .ConfigureAwait(false);

        // A console that answered locally needs nothing more, and a local wake that timed out is a console we
        // can see and that is refusing to come up — asking the cloud to wake something already within reach
        // would not help and would cost a round trip on every attempt.
        if (local is ConsoleWakeOutcome.AlreadyAwake or ConsoleWakeOutcome.Woken or ConsoleWakeOutcome.TimedOut)
        {
            return local;
        }

        if (!CanWakeRemotely(console))
        {
            return local;
        }

        progress?.Report("Asking PlayStation Network to wake your console…");

        try
        {
            await _account.WakeAsync(console.CloudDeviceId!, cancellationToken).ConfigureAwait(false);
            return ConsoleWakeOutcome.AskedRemotely;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The remote wake is a bonus attempt on a path that had already failed locally. Its own failure
            // must not replace the local outcome with something worse, and must not throw out of what the
            // caller asked for — connect proceeds and reports its own, clearer failure.
            return local;
        }
    }

    /// <summary>
    /// Whether the remote path is even available for this console: we must know how the account service
    /// addresses it, and be signed in to ask.
    /// </summary>
    private bool CanWakeRemotely(PairedConsole console)
        => !string.IsNullOrWhiteSpace(console.CloudDeviceId)
           && _account.CanSignIn
           && _account.Current is not null;
}
