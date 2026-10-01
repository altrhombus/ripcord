namespace Ripcord.Core.Input;

/// <summary>One pad's reading from a poll: which device it came from, and its state.</summary>
/// <param name="DeviceId">
/// Stable for as long as the device stays connected, and never reused within a process: a pad that is
/// unplugged and plugged back in comes back under a new id, which is what makes it a new connection.
/// </param>
public readonly record struct GamepadReading(ulong DeviceId, ControllerStateFrame Frame);

/// <summary>What one poll changed: pads that appeared, pads that went, and the merged frame.</summary>
/// <param name="Merged">Every connected pad merged into one frame, or null when no pad is connected.</param>
public sealed record GamepadSetUpdate(
    IReadOnlyList<ulong> Connected,
    IReadOnlyList<ulong> Disconnected,
    ControllerStateFrame? Merged);

/// <summary>
/// Turns a poll of every connected gamepad into connection changes and one merged frame.
///
/// <para>
/// <b>Why one engine merges its own pads rather than publishing a frame per pad.</b> The composite above keeps
/// the latest frame per engine, not per device, so two pads publishing through one engine would overwrite each
/// other: a button held on one would read as released every time the other reported. Merging here, with the
/// same rule the composite uses between engines, keeps "several controllers act as one pad" true for pads on
/// the same engine as well as pads on different ones.
/// </para>
///
/// <para>
/// Pure and single-threaded: the caller polls from one timer, so there is no lock. It lives in Core rather than
/// beside the GameInput engine so the platform-neutral test suite can reach it.
/// </para>
/// </summary>
public sealed class GamepadSet
{
    private readonly HashSet<ulong> _connected = [];

    /// <summary>The ids currently connected, as of the last <see cref="Update"/>.</summary>
    public IReadOnlyCollection<ulong> Connected => _connected;

    public GamepadSetUpdate Update(IReadOnlyList<GamepadReading> readings)
    {
        var seen = new HashSet<ulong>();
        var connected = new List<ulong>();
        ControllerStateFrame? merged = null;

        foreach (GamepadReading reading in readings)
        {
            if (!seen.Add(reading.DeviceId))
            {
                // The same device twice in one poll is a reader bug, but merging it twice would be a no-op anyway
                // (OR and further-from-centre are idempotent), so it is skipped rather than thrown on.
                continue;
            }

            if (_connected.Add(reading.DeviceId))
            {
                connected.Add(reading.DeviceId);
            }

            merged = merged is { } m ? ControllerInputMerger.Merge(m, reading.Frame) : reading.Frame;
        }

        var disconnected = _connected.Where(id => !seen.Contains(id)).ToList();
        foreach (ulong id in disconnected)
        {
            _connected.Remove(id);
        }

        return new GamepadSetUpdate(connected, disconnected, merged);
    }
}
