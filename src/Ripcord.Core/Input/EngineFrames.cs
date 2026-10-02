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
/// Not thread-safe: the composite calls it under its own gate. Portable, and so tested, which the composite
/// itself can't be: it lives in the Windows-only input assembly.
/// </para>
/// </summary>
public sealed class EngineFrames
{
    private readonly Dictionary<string, ControllerStateFrame> _latest = [];

    /// <summary>Record <paramref name="engine"/>'s newest frame and return the merge of every engine's.</summary>
    public ControllerStateFrame Set(string engine, ControllerStateFrame frame)
    {
        _latest[engine] = frame;
        return Merged(frame.TimestampTicks);
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
