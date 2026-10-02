namespace Ripcord.Core.Input;

/// <summary>
/// The latest frame from each input engine (GameInput, the DualSense HID reader, the keyboard), and their merge.
///
/// <para>
/// <b>It forgets an engine.</b> The composite used to keep every engine's last frame for good, so a pad pulled
/// mid-press went on OR-ing that press into every merged frame: the button stayed held in the stream. GameInput's
/// own neutral frame on its last disconnect hid that for one engine; the DualSense never sent one (the 2026-09-30
/// review). <see cref="Forget"/> drops the engine, so the merge no longer depends on any engine remembering to
/// clear itself.
/// </para>
///
/// <para>
/// <b>And it knows which engine is in use.</b> <see cref="Active"/> is the engine that last showed a deliberate
/// press, so with two pads attached the prompts can name the one in hand rather than the one that connected last
/// (2026-10-02). One pad can reach two engines (GameInput enumerates a DualSense that raw HID also reads), so a
/// press another engine already holds goes to whichever of them comes first in the precedence the composite gives:
/// otherwise the engine whose report happened to land second would win, and the names would follow the race.
/// </para>
///
/// <para>
/// Not thread-safe: the composite calls it under its own gate. Portable, and so tested, which the composite
/// itself can't be: it lives in the Windows-only input assembly.
/// </para>
/// </summary>
public sealed class EngineFrames
{
    /// <summary>How far a stick or trigger travels before moving it counts as reaching for that pad.</summary>
    public const float DeliberateTravel = 0.5f;

    private readonly Dictionary<string, ControllerStateFrame> _latest = [];
    private readonly string[] _precedence;

    /// <summary>
    /// <paramref name="precedence"/> orders the engines for a press two of them report (see the class note);
    /// an engine not named comes after every one that is.
    /// </summary>
    public EngineFrames(params string[] precedence) => _precedence = precedence;

    /// <summary>
    /// The engine that last showed a deliberate press (<see cref="IsDeliberate"/>), or null before any has. Kept
    /// when that engine is forgotten: a pad that goes to sleep should not relabel what is on screen.
    /// </summary>
    public string? Active { get; private set; }

    /// <summary>Record <paramref name="engine"/>'s newest frame and return the merge of every engine's.</summary>
    public ControllerStateFrame Set(string engine, ControllerStateFrame frame)
    {
        ControllerStateFrame? previous = _latest.TryGetValue(engine, out ControllerStateFrame last) ? last : null;
        if (IsDeliberate(previous, frame))
        {
            Active = Claimant(engine, previous, frame);
        }

        _latest[engine] = frame;
        return Merged(frame.TimestampTicks);
    }

    /// <summary>
    /// Whether going from <paramref name="previous"/> to <paramref name="current"/> is someone using the pad: a
    /// button newly down, or a stick or trigger newly past <see cref="DeliberateTravel"/>. Not any change, because
    /// a pad at rest still reports: sticks drift and a DualSense streams its motion sensors, and either would
    /// claim the pad was in use while it sat on the table.
    /// </summary>
    /// <summary>
    /// Who a press on <paramref name="engine"/> belongs to: that engine, unless another engine already holds the same
    /// press and comes before it in the precedence, in which case it is one pad seen twice and that engine has it.
    /// </summary>
    private string Claimant(string engine, ControllerStateFrame? previous, ControllerStateFrame frame)
    {
        ControllerButtons fresh = frame.Buttons & ~(previous?.Buttons ?? ControllerButtons.None);
        string claimant = engine;

        foreach ((string other, ControllerStateFrame held) in _latest)
        {
            bool samePress = fresh != ControllerButtons.None
                ? (held.Buttons & fresh) == fresh
                : PastTravel(held) && PastTravel(frame);

            if (other != engine && samePress && Rank(other) < Rank(claimant))
            {
                claimant = other;
            }
        }

        return claimant;

        static bool PastTravel(ControllerStateFrame f)
            => Math.Abs(f.LeftStickX) >= DeliberateTravel || Math.Abs(f.LeftStickY) >= DeliberateTravel
               || Math.Abs(f.RightStickX) >= DeliberateTravel || Math.Abs(f.RightStickY) >= DeliberateTravel
               || f.LeftTrigger >= DeliberateTravel || f.RightTrigger >= DeliberateTravel;
    }

    private int Rank(string engine)
    {
        int index = Array.IndexOf(_precedence, engine);
        return index < 0 ? int.MaxValue : index;
    }

    public static bool IsDeliberate(ControllerStateFrame? previous, ControllerStateFrame current)
    {
        ControllerStateFrame before = previous ?? default;

        return (current.Buttons & ~before.Buttons) != 0
               || Crossed(before.LeftStickX, current.LeftStickX)
               || Crossed(before.LeftStickY, current.LeftStickY)
               || Crossed(before.RightStickX, current.RightStickX)
               || Crossed(before.RightStickY, current.RightStickY)
               || Crossed(before.LeftTrigger, current.LeftTrigger)
               || Crossed(before.RightTrigger, current.RightTrigger);

        static bool Crossed(float was, float now)
            => Math.Abs(now) >= DeliberateTravel && Math.Abs(was) < DeliberateTravel;
    }

    /// <summary>
    /// Drop <paramref name="engine"/>. True when it held a frame, with the merge of what remains in
    /// <paramref name="merged"/>: neutral when nothing does.
    /// </summary>
    public bool Forget(string engine, long nowTicks, out ControllerStateFrame merged)
    {
        if (!_latest.Remove(engine))
        {
            merged = default;
            return false;
        }

        merged = Merged(nowTicks);
        return true;
    }

    private ControllerStateFrame Merged(long nowTicks)
        => _latest.Count == 0
            ? default(ControllerStateFrame) with { TimestampTicks = nowTicks }
            : _latest.Values.Aggregate(ControllerInputMerger.Merge);
}
