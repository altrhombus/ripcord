namespace Ripcord.Core.Input;

/// <summary>
/// Applies a gamepad remap, and merges a pad frame with a keyboard frame into the single frame the session sends.
///
/// <para>
/// Merging is necessary rather than nice: the session takes one stream of frames, and each frame is <em>absolute
/// state</em>, not a delta. Forwarding whichever source spoke last would mean the pad's resting frame — sticks
/// centred, no buttons — immediately cancelling a held key, and vice versa, so both sources would fight at
/// whatever rate they happen to report. Combining the latest of each is the only behaviour that lets someone use
/// a keyboard and a pad at the same time, which matters on a handheld docked to a desk.
/// </para>
/// </summary>
public static class ControllerInputMerger
{
    /// <summary>
    /// Relabel buttons through <paramref name="remap"/>. Absent entries pass through, so a device only needs
    /// entries for the buttons it gets wrong.
    /// </summary>
    public static ControllerStateFrame ApplyRemap(
        ControllerStateFrame frame, IReadOnlyDictionary<ControllerButtons, ControllerButtons> remap)
    {
        if (remap.Count == 0)
        {
            return frame;
        }

        ControllerButtons remapped = ControllerButtons.None;
        for (int bit = 0; bit < 32; bit++)
        {
            var flag = (ControllerButtons)(1u << bit);
            if ((frame.Buttons & flag) == 0)
            {
                continue;
            }

            // A button with no remap entry keeps its own identity. Note this reads from the ORIGINAL frame for
            // every bit, so a swap (South->East, East->South) works instead of one overwriting the other.
            remapped |= remap.TryGetValue(flag, out ControllerButtons target) ? target : flag;
        }

        return frame with { Buttons = remapped };
    }

    /// <summary>
    /// Combine two frames. Buttons are OR-ed; axes take whichever source is further from centre, so a pad resting
    /// at zero never cancels a keyboard direction and a real stick always wins over a key's full deflection when
    /// both are in use. The timestamp is the later of the two.
    /// </summary>
    public static ControllerStateFrame Merge(ControllerStateFrame a, ControllerStateFrame b) => new(
        Math.Max(a.TimestampTicks, b.TimestampTicks),
        a.Buttons | b.Buttons,
        FurtherFromCentre(a.LeftStickX, b.LeftStickX),
        FurtherFromCentre(a.LeftStickY, b.LeftStickY),
        FurtherFromCentre(a.RightStickX, b.RightStickX),
        FurtherFromCentre(a.RightStickY, b.RightStickY),
        Math.Max(a.LeftTrigger, b.LeftTrigger),
        Math.Max(a.RightTrigger, b.RightTrigger),
        // Motion and touch come only from real hardware, so take whichever frame actually has them.
        a.Gyro ?? b.Gyro,
        a.Accel ?? b.Accel,
        a.Touchpad ?? b.Touchpad);

    /// <summary>
    /// Add on-screen ("virtual") buttons to a frame.
    ///
    /// <para>
    /// Additive, never replacing: tapping the on-screen PS button while holding a stick direction must send both.
    /// Lives here rather than inside the merged source so the semantics are testable — the source itself is in a
    /// Windows-targeted assembly the platform-neutral test suite cannot reference.
    /// </para>
    /// </summary>
    public static ControllerStateFrame WithVirtualButtons(ControllerStateFrame frame, ControllerButtons virtualButtons)
        => virtualButtons == ControllerButtons.None
            ? frame
            : frame with { Buttons = frame.Buttons | virtualButtons };

    private static float FurtherFromCentre(float a, float b) => MathF.Abs(a) >= MathF.Abs(b) ? a : b;
}
