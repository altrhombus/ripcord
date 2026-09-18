namespace Ripcord.Protocol.Halyard.Common.Discovery;

/// <summary>What happened when we tried to make sure a console was awake before connecting.</summary>
public enum WakeOutcome
{
    /// <summary>The console already answered discovery as awake; nothing was sent.</summary>
    AlreadyAwake,

    /// <summary>The console was in standby, we sent a WAKEUP, and it came up.</summary>
    Woke,

    /// <summary>The console was in standby and did not come up within the budget.</summary>
    TimedOut,

    /// <summary>
    /// The console did not answer discovery at all. Not treated as a failure: it may be awake but slow to
    /// answer, off the LAN, or reachable only by its known address — so the caller should still attempt the
    /// connection rather than block on a wake that may not be needed.
    /// </summary>
    NotFound,
}

/// <summary>
/// Ensures a console is awake before a session tries to connect: probe discovery, and if the console reports
/// standby, send a LAN <c>WAKEUP</c> and poll until it reports awake. The wake protocol itself is
/// <see cref="HalyardWakeClient"/>; this is the orchestration around it.
///
/// <para>
/// The two I/O operations are injected rather than hard-wired to the concrete clients, so the decision logic
/// — which of the four <see cref="WakeOutcome"/>s applies — is exercised by tests without opening a socket.
/// The app composes the real probe (a targeted SRCH read) and the real wake sender.
/// </para>
/// </summary>
public sealed class HalyardWakeCoordinator(
    Func<CancellationToken, Task<bool?>> probeAwake,
    Func<CancellationToken, Task> sendWake,
    TimeSpan? pollInterval = null,
    TimeSpan? wakeBudget = null,
    TimeProvider? timeProvider = null)
{
    private readonly Func<CancellationToken, Task<bool?>> _probeAwake = probeAwake;
    private readonly Func<CancellationToken, Task> _sendWake = sendWake;

    // The clock this loop measures its budget against, injectable for the same reason HalyardWanRendezvous
    // takes one: a poll loop whose exit depends on elapsed real time cannot be tested without either waiting
    // out the real interval or racing it. Tests supply a clock that reports the delay as having happened
    // without spending it, so what decides the outcome is the SEQUENCE of probe answers - which is what this
    // class is about - rather than how fast the host got through them.
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // A few seconds between polls: a PS5 takes real time to come out of rest mode, so hammering discovery
    // buys nothing. The budget is generous because failing to wake a console the user asked for is worse than
    // waiting — but bounded, so a console that will never wake does not hang the connect forever.
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    private readonly TimeSpan _wakeBudget = wakeBudget ?? TimeSpan.FromSeconds(30);

    /// <summary>
    /// <paramref name="probeAwake"/> returns true (awake), false (standby), or null (no reply). Progress
    /// strings, when a sink is supplied, are user-facing status lines.
    /// </summary>
    public async Task<WakeOutcome> EnsureAwakeAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        bool? awake = await _probeAwake(cancellationToken).ConfigureAwait(false);
        if (awake is null)
        {
            // Do not send a wake to a console we cannot see — the credential is right but the address may not
            // be, and connect will surface a real error faster than a wake that lands nowhere.
            return WakeOutcome.NotFound;
        }

        if (awake.Value)
        {
            return WakeOutcome.AlreadyAwake;
        }

        progress?.Report("Waking your console…");
        await _sendWake(cancellationToken).ConfigureAwait(false);

        // Poll until it reports awake or the budget runs out. A single WAKEUP is enough in the capture, so we
        // do not re-send on every tick — a console that ignored the first is unlikely to answer a flood, and
        // re-sending risks nothing useful.
        var deadline = _time.GetUtcNow() + _wakeBudget;
        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(_pollInterval, _time, cancellationToken).ConfigureAwait(false);

            bool? state = await _probeAwake(cancellationToken).ConfigureAwait(false);
            if (state is true)
            {
                return WakeOutcome.Woke;
            }

            progress?.Report("Waiting for the console to wake…");
        }

        return WakeOutcome.TimedOut;
    }
}
