using Ripcord.Core.Sessions;

namespace Ripcord.Client;

/// <summary>
/// Where a managed session is in its life. Distinct from <see cref="SessionState"/>, which describes a single
/// <see cref="IStreamingSession"/> instance: the controller outlives individual sessions (that is the whole
/// point of reconnect), so it needs <see cref="Idle"/> and <see cref="Failed"/> states no session object has.
/// </summary>
public enum SessionLifecycle
{
    /// <summary>Nothing running; nothing has been attempted yet.</summary>
    Idle,

    /// <summary>First connection attempt in progress.</summary>
    Connecting,

    /// <summary>Connected with video flowing.</summary>
    Streaming,

    /// <summary>Connected, but video has stopped arriving. Recoverable without a reconnect — often is.</summary>
    Degraded,

    /// <summary>The session was torn down and a new one is being established, with backoff.</summary>
    Reconnecting,

    /// <summary>Given up: either not retryable, or the retry budget is exhausted. Terminal until restarted.</summary>
    Failed,

    /// <summary>Stopped deliberately. Terminal.</summary>
    Closed,
}

/// <summary>
/// A snapshot of the controller's state, shaped for direct display. <see cref="Detail"/> is written for a
/// player, not an engineer — the UI renders it verbatim rather than deriving its own wording, so that every
/// front end says the same thing.
/// </summary>
/// <param name="Lifecycle">Current lifecycle state.</param>
/// <param name="Detail">Plain-language description of what is happening and, on failure, what to do.</param>
/// <param name="ReconnectAttempt">1-based attempt number while reconnecting; 0 otherwise.</param>
/// <param name="NextRetryIn">How long until the next attempt, when reconnecting and waiting.</param>
public sealed record SessionStatus(
    SessionLifecycle Lifecycle,
    string Detail,
    int ReconnectAttempt = 0,
    TimeSpan? NextRetryIn = null)
{
    /// <summary>True while a stream is (or should be) on screen — i.e. chrome should stay out of the way.</summary>
    public bool IsLive => Lifecycle is SessionLifecycle.Streaming or SessionLifecycle.Degraded;

    /// <summary>True once the controller has stopped trying, so the UI can offer a retry affordance.</summary>
    public bool IsTerminal => Lifecycle is SessionLifecycle.Failed or SessionLifecycle.Closed;
}

/// <summary>
/// Tunables for the lifecycle policy. Defaults are chosen for LAN remote play, where a brief Wi-Fi hiccup is
/// common and worth riding out, but a genuinely dead console should be reported rather than retried forever.
/// </summary>
public sealed record SessionControllerOptions
{
    /// <summary>No video for this long while streaming ⇒ <see cref="SessionLifecycle.Degraded"/>.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// No video for this long ⇒ tear down and reconnect. Deliberately longer than <see cref="StallTimeout"/>:
    /// most stalls are a dropped IDR or a momentary Wi-Fi dip and recover on their own, and reconnecting is far
    /// more disruptive (it drops the session on the console) than waiting a few seconds.
    /// </summary>
    public TimeSpan ReconnectAfterStall { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>How often the watchdog evaluates liveness.</summary>
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>First reconnect delay; doubles each attempt up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on the backoff delay.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Consecutive failed attempts before giving up. Finite so the UI eventually shows an actionable failure
    /// instead of spinning indefinitely while the user waits for something that is never coming back.
    /// </summary>
    public int MaxReconnectAttempts { get; init; } = 6;
}
