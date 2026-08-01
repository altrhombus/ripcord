namespace Ripcord.Core.Input;

/// <summary>
/// Platform-neutral controller state snapshot. Lives in Core (rather than Ripcord.Input) so both
/// <see cref="Sessions.ISessionInputSink"/> (Core) and Ripcord.Input's IControllerSource can share
/// it without Core depending on Input. Each protocol backend owns the mapping from this shape to
/// its own wire format - that mapping is what lets a second console family's backend slot in later
/// without reworking the first one.
/// </summary>
public readonly record struct ControllerStateFrame(
    long TimestampTicks,
    ControllerButtons Buttons,
    float LeftStickX,
    float LeftStickY,
    float RightStickX,
    float RightStickY,
    float LeftTrigger,
    float RightTrigger,
    GyroSample? Gyro,
    AccelSample? Accel,
    TouchpadSample? Touchpad);

[Flags]
public enum ControllerButtons : uint
{
    None = 0,
    South = 1 << 0,
    East = 1 << 1,
    West = 1 << 2,
    North = 1 << 3,
    DPadUp = 1 << 4,
    DPadDown = 1 << 5,
    DPadLeft = 1 << 6,
    DPadRight = 1 << 7,
    LeftShoulder = 1 << 8,
    RightShoulder = 1 << 9,
    LeftStick = 1 << 10,
    RightStick = 1 << 11,
    Start = 1 << 12,
    Select = 1 << 13,
    Guide = 1 << 14,
    TouchpadClick = 1 << 15,
}

public readonly record struct GyroSample(float X, float Y, float Z);

public readonly record struct AccelSample(float X, float Y, float Z);

public readonly record struct TouchpadSample(float X, float Y, bool Touching);
