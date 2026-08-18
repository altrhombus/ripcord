using Ripcord.Core.Consoles;

namespace Ripcord.Presentation.Sessions;

/// <summary>What happened when we tried to make sure the console was awake.</summary>
public enum ConsoleWakeOutcome
{
    /// <summary>It was already on. Nothing was sent.</summary>
    AlreadyAwake,

    /// <summary>It was in standby, was sent a wake packet, and came up.</summary>
    Woken,

    /// <summary>It reported standby and did not come up within the coordinator's budget.</summary>
    TimedOut,

    /// <summary>
    /// We could not tell — most often because the stored pairing has no usable wake credential. Distinct from
    /// <see cref="TimedOut"/> because it is not a reason to stop: connect will fail with its own, clearer
    /// message about the pairing than anything this layer could invent about the power state.
    /// </summary>
    Unknown,

    /// <summary>
    /// The account service accepted a wake request for this console, and whether it actually came up is
    /// unverified.
    ///
    /// <para>
    /// Its own value rather than <see cref="Woken"/> because we genuinely do not know: the remote wake is
    /// delivered through PSN's own server-side fan-out, so acceptance says the request was queued and nothing
    /// more. Claiming <see cref="Woken"/> here would be the kind of over-confident status string this app has
    /// already lost hours to. Like <see cref="Unknown"/>, it is not a reason to stop.
    /// </para>
    /// </summary>
    AskedRemotely,
}

/// <summary>
/// Makes sure a console is awake before a connect is attempted.
///
/// <para>
/// A connect to a sleeping console cannot succeed, and before this existed it simply hung on "Connecting…"
/// until timeout with no cause offered — which is the failure this seam is really about. The mechanism is
/// deeply family-specific (a PS4 wakes on one port and protocol version, a PS5 on another) and needs the
/// console's stored credential to sign the wake, so all of it belongs on the protocol side.
/// </para>
///
/// <para>
/// What is left up here is the part worth testing: what the user is told while it happens, and what the session
/// does with each outcome.
/// </para>
/// </summary>
public interface IConsoleWakeCoordinator
{
    /// <summary>
    /// Wake <paramref name="console"/> if it is resting, reporting progress as it goes.
    /// </summary>
    /// <param name="progress">
    /// Receives short lines meant to be shown to a waiting user. Optional: the outcome is the contract, the
    /// commentary is a courtesy.
    /// </param>
    Task<ConsoleWakeOutcome> EnsureAwakeAsync(
        PairedConsole console,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
