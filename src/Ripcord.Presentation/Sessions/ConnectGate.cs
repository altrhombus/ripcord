namespace Ripcord.Presentation.Sessions;

/// <summary>
/// Decides when a connect stage has earned the screen.
///
/// <para>
/// <b>The problem this exists for.</b> <see cref="ConnectFlow"/> announces every phase <em>before</em> it runs,
/// which is right and must not change: when something hangs, the last line on screen names it, and a video
/// device stalling on unfamiliar hardware used to be indistinguishable from a network problem because the
/// overlay said "Connecting…" throughout. But on a LAN connect to an awake console the whole sequence is over
/// in under two seconds, so the player reads three headlines at title size in the time it takes to sit down.
/// That is a strobe, and it is the <em>common</em> case — the one the composition exists to keep calm.
/// </para>
///
/// <para>
/// <b>So the fix is not to say less, it is to refuse to promote a line that has not earned the screen.</b> A
/// stage's text appears once that stage has been running for <see cref="Dwell"/>; before that the headline
/// holds whatever was already there, which at the start of a connect is the console's own name. A fast connect
/// shows one calm line and cuts to the game. A hang shows exactly what it shows today.
/// </para>
///
/// <para>
/// <b>Two things bypass the dwell entirely</b>, and both are the same rule seen twice: a line the user has to
/// act on is never withheld. A terminal stage is the end of the sequence and carries the reason it stopped, and
/// a stage that <em>escalates</em> the phase is the only evidence the player has that a long wait has begun —
/// withholding "Waking your PS5…" for half a second would be withholding it at the exact moment it starts
/// mattering.
/// </para>
///
/// <para>
/// Pure and clock-driven, like <see cref="HealthAlertGate"/>, which is the same idea applied to the other end
/// of the session: both answer "has this message earned an interruption?" and neither owns a timer.
/// </para>
/// </summary>
public sealed class ConnectGate
{
    /// <summary>
    /// How long a stage must be the current one before its text is shown.
    ///
    /// <para>
    /// Long enough that the three sub-second phases of a warm LAN connect never appear, short enough that a
    /// stage which is genuinely making someone wait names itself well before they wonder. The recorded live
    /// handshake is 2.1 ms and the whole warm path is a small number of hundreds of milliseconds, so this
    /// clears all of it with room to spare.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(600);

    private ConnectStage? _pending;
    private DateTimeOffset _pendingSince;

    /// <summary>The stage currently on screen. Null until one has earned it.</summary>
    public ConnectStage? Shown { get; private set; }

    /// <summary>
    /// Offer a stage. Returns the stage that should be on screen now, or <see langword="null"/> if nothing has
    /// earned the screen yet and the caller should leave what it has alone.
    /// </summary>
    /// <param name="stage">The stage <see cref="ConnectFlow"/> just reported.</param>
    /// <param name="now">Report time, from the caller's injected clock.</param>
    public ConnectStage? Offer(ConnectStage stage, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(stage);

        // A terminal stage is the end of the sequence: it carries the reason, and the retry/leave actions
        // appear with it. Nothing about it is worth delaying.
        if (stage.Terminal)
        {
            _pending = null;
            Shown = stage;
            return Shown;
        }

        // An escalation is the start of a wait the player can feel. Preparing → Waking is the difference
        // between "this is taking a moment" and "the console is asleep and this will take twenty seconds";
        // the dwell must not swallow the announcement of a wait.
        if (Shown is { } shown && stage.Phase > shown.Phase)
        {
            _pending = null;
            Shown = stage;
            return Shown;
        }

        // Same stage as the one already pending: keep the original start time, so a flow that re-reports a
        // phase (the wake coordinator's progress lines do) does not reset its own wait.
        if (_pending is null || !SameLine(_pending, stage))
        {
            _pending = stage;
            _pendingSince = now;
        }
        else
        {
            _pending = stage;
        }

        if (now - _pendingSince >= Dwell)
        {
            Shown = _pending;
            _pending = null;
        }

        return Shown;
    }

    /// <summary>
    /// Let time pass without a new stage. The flow only reports when something changes, so a phase that hangs
    /// reports once and then says nothing — without this, a stage that took four seconds would never be
    /// promoted, which is precisely backwards.
    /// </summary>
    /// <param name="now">Current time, from the caller's injected clock.</param>
    /// <returns>The stage that should be on screen now.</returns>
    public ConnectStage? Tick(DateTimeOffset now)
    {
        if (_pending is { } pending && now - _pendingSince >= Dwell)
        {
            Shown = pending;
            _pending = null;
        }

        return Shown;
    }

    /// <summary>
    /// Forget everything. For a new connect: the previous attempt's last line must not be on screen while the
    /// next one is deciding what to say.
    /// </summary>
    public void Reset()
    {
        _pending = null;
        Shown = null;
        _pendingSince = default;
    }

    /// <summary>
    /// Whether two stages say the same thing. Compared by what the player reads rather than by reference,
    /// because the flow builds a fresh record every time it reports.
    /// </summary>
    private static bool SameLine(ConnectStage a, ConnectStage b)
        => a.Headline == b.Headline && a.Detail == b.Detail && a.Phase == b.Phase;
}
