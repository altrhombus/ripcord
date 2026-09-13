namespace Ripcord.Presentation.Sessions;

using Ripcord.Core.Sessions;

/// <summary>
/// Decides whether the stream's health is worth interrupting the player about.
///
/// <para>
/// <b>Why a gate and not just the verdict.</b> <see cref="StreamHealthAssessor"/> answers "how is the stream
/// right now", twice a second, and it is right to be that twitchy — the diagnostics panel wants the live
/// value. But rung 1 of the HUD is the opposite thing: a single line that appears over the game unbidden. A
/// banner driven straight from the verdict would flash on every two-second wobble, and a notice that cries
/// wolf is worse than no notice, because the one time it matters the player has already learned to ignore it.
/// </para>
///
/// <para>
/// So this is hysteresis, and it is deliberately asymmetric in two directions:
/// </para>
/// <list type="bullet">
///   <item><b>Critical rises faster than Warning.</b> "The stream has stopped" should not sit behind a
///   five-second wait — and the assessor has already applied its own two-second stall grace before it says
///   so, so any delay here is additive to one the player has already spent staring at a frozen picture.</item>
///   <item><b>Clearing is slower than either rise.</b> That relationship is the anti-strobe invariant: if it
///   cleared faster than it raised, a stream flapping across a threshold would blink the banner on and off.
///   Settling into "shown" is the correct failure, because the stream genuinely is unwell.</item>
/// </list>
///
/// <para>
/// Pure and clock-driven, like the assessor it sits on top of: no timers, no state beyond two fields, every
/// threshold reachable from a test without waiting for wall-clock time to pass.
/// </para>
/// </summary>
public sealed class HealthAlertGate
{
    /// <summary>
    /// How long a <see cref="StreamHealthLevel.Warning"/> must persist before it is worth saying. Longer than
    /// the two-second blip the design explicitly refuses to raise a banner for.
    /// </summary>
    public static readonly TimeSpan RaiseAfterWarning = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a <see cref="StreamHealthLevel.Critical"/> must persist. Short, because the assessor has
    /// already spent its own grace period deciding the stream is broken rather than merely quiet.
    /// </summary>
    public static readonly TimeSpan RaiseAfterCritical = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the stream must stay well before the notice goes away. Longer than either rise window — see
    /// the anti-strobe note on the class.
    /// </summary>
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(6);

    /// <summary>When the current run of "unwell" or "well" began. Null until the first sample.</summary>
    private DateTimeOffset? _runStartedAt;

    /// <summary>Whether the run in progress is an unwell one. Meaningless while <see cref="_runStartedAt"/> is null.</summary>
    private bool _runIsUnwell;

    /// <summary>Whether the notice is currently showing.</summary>
    public bool IsRaised { get; private set; }

    /// <summary>
    /// Feed one verdict. Returns <see cref="IsRaised"/> after the update, so a caller can drive state from the
    /// return value without a second read.
    /// </summary>
    /// <param name="level">The assessor's current verdict.</param>
    /// <param name="now">Sample time, from the caller's injected clock.</param>
    public bool Update(StreamHealthLevel level, DateTimeOffset now)
    {
        // Info is not a problem. It covers "Starting up…" and "hardware decode with a memory copy" — states
        // that are either temporary by construction or simply worth knowing, and neither earns an interruption.
        bool unwell = level is StreamHealthLevel.Warning or StreamHealthLevel.Critical;

        if (_runStartedAt is null || _runIsUnwell != unwell)
        {
            _runStartedAt = now;
            _runIsUnwell = unwell;
        }

        TimeSpan held = now - _runStartedAt.Value;

        if (unwell)
        {
            // A run that begins Warning and escalates to Critical keeps its start time, so the escalation is
            // credited with the time already served rather than restarting the wait. A player watching a
            // stream degrade should not be told later for having watched it degrade sooner.
            TimeSpan required = level == StreamHealthLevel.Critical ? RaiseAfterCritical : RaiseAfterWarning;
            if (held >= required)
            {
                IsRaised = true;
            }
        }
        else if (IsRaised && held >= ClearAfter)
        {
            IsRaised = false;
        }

        return IsRaised;
    }

    /// <summary>
    /// Forget everything. For a new session: the previous one's final verdict must not carry a banner into the
    /// first seconds of the next, when nothing has been measured yet.
    /// </summary>
    public void Reset()
    {
        _runStartedAt = null;
        _runIsUnwell = false;
        IsRaised = false;
    }
}
