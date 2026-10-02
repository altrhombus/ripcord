namespace Ripcord.Presentation.Sessions;

using Ripcord.Core.Consoles;
using Ripcord.Core.Sessions;
using Ripcord.Presentation.Resources;

/// <summary>
/// The session factory a reconnect uses: every open after the first asks the console to wake first.
///
/// <para>
/// <b>Why.</b> <see cref="ConnectFlow"/> checks the console's power state once, before the first attempt, and the
/// controller's retries only ever reopened the session. A console going into rest as the connect began (the one
/// the last session asked to rest, still on its way down) answered discovery as awake, dropped the first attempt,
/// and refused every retry after it: six "connection refused" in a row, then "Couldn't connect", while backing
/// out and connecting again found it resting and woke it (2026-10-02). Asking before each retry is what the second
/// connect did, inside the first one.
/// </para>
///
/// <para>
/// The first open goes straight through because the flow has just asked. A console that is awake answers the
/// check at once, so a retry against one costs a discovery round trip and nothing more.
/// </para>
/// </summary>
public static class WakeBeforeRetry
{
    /// <summary>
    /// Wrap <paramref name="open"/> so every call after the first wakes <paramref name="console"/> first,
    /// reporting the wake's lines to <paramref name="progress"/>.
    /// </summary>
    /// <remarks>
    /// A console that reported standby and never came up fails that open with <see cref="Strings.Connect_DidNotWakeDetail"/>,
    /// which the controller reports and retries like any other failed open. Every other outcome opens as before:
    /// a console whose power state cannot be told may still be reachable, and the open says more about why not.
    /// </remarks>
    public static Func<CancellationToken, Task<IStreamingSession>> Wrap(
        Func<CancellationToken, Task<IStreamingSession>> open,
        IConsoleWakeCoordinator wake,
        PairedConsole console,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(wake);
        ArgumentNullException.ThrowIfNull(console);

        int opens = 0;

        return async cancellationToken =>
        {
            // The controller opens one session at a time, so a plain counter is enough.
            if (opens++ > 0)
            {
                ConsoleWakeOutcome outcome = await wake.EnsureAwakeAsync(console, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (outcome == ConsoleWakeOutcome.TimedOut)
                {
                    throw new InvalidOperationException(Strings.Connect_DidNotWakeDetail);
                }
            }

            return await open(cancellationToken).ConfigureAwait(false);
        };
    }
}
