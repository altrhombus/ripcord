using Ripcord.Core.Input;

namespace Ripcord.Input.Common;

/// <summary>
/// Parses a DualSense input report into a neutral <see cref="ControllerStateFrame"/>. Pure and
/// platform-neutral so it is unit-testable off-device; the Windows-only HID transport in Ripcord.Input feeds
/// it raw report bytes. Field offsets were reversed from live USB and Bluetooth captures (differential
/// analysis) and cross-checked against the public DualSense/DS4 layouts.
///
/// <para>Three report formats occur:</para>
/// <list type="bullet">
///   <item><description><b>USB full</b> — report id <c>0x01</c>, 64 bytes: field block right after the id,
///   with sticks, triggers, buttons, IMU and touchpad.</description></item>
///   <item><description><b>Bluetooth full</b> — report id <c>0x31</c>: one extra lead byte before the same
///   full field block, plus a trailing CRC. Only sent after the controller is "activated" (a feature-report
///   <c>0x05</c> read).</description></item>
///   <item><description><b>Compatibility</b> — report id <c>0x01</c> in the DS4-style compact layout Windows
///   delivers over Bluetooth by default (and any short <c>0x01</c>): sticks + buttons + triggers only, no
///   touchpad position or motion. Distinguished from USB full by length (USB full is exactly 64 bytes).</description></item>
/// </list>
///
/// <para>Stick Y is inverted so the neutral frame uses stick-up = +1 (the shared convention across every
/// controller source); the protocol layer re-inverts for the console wire. Motion (gyro/accel) is present in
/// the full reports but left unmapped until calibrated, so <see cref="ControllerStateFrame.Gyro"/>/
/// <see cref="ControllerStateFrame.Accel"/> are null.</para>
/// </summary>
public static class DualSenseReportParser
{
    public const byte UsbReportId = 0x01;
    public const byte BluetoothFullReportId = 0x31;
    public const int UsbReportLength = 64;

    // ---- full report field offsets (relative to the field-block start) ----
    private const int LeftStickX = 0;
    private const int LeftStickY = 1;
    private const int RightStickX = 2;
    private const int RightStickY = 3;
    private const int LeftTrigger = 4;
    private const int RightTrigger = 5;
    private const int Buttons0 = 7;   // dpad hat (low nibble) + face buttons (high nibble)
    private const int Buttons1 = 8;   // L1/R1/L2/R2/Create/Options/L3/R3
    private const int Buttons2 = 9;   // PS / touchpad-click / mute
    private const int Touch0 = 32;    // first touch point: [id/active][x lo][x hi|y lo][y hi]
    private const int FullBodyLength = 36;

    // ---- compact (Bluetooth compatibility / DS4-style) field offsets ----
    private const int CompactButtons0 = 4;
    private const int CompactButtons1 = 5;
    private const int CompactButtons2 = 6;
    private const int CompactLeftTrigger = 7;
    private const int CompactRightTrigger = 8;
    private const int CompactBodyLength = 9;

    // Face-button masks (buttons0, same in full and compact).
    private const byte Square = 0x10;
    private const byte Cross = 0x20;
    private const byte Circle = 0x40;
    private const byte Triangle = 0x80;

    // buttons1 masks.
    private const byte L1 = 0x01;
    private const byte R1 = 0x02;
    private const byte Create = 0x10;
    private const byte Options = 0x20;
    private const byte L3 = 0x40;
    private const byte R3 = 0x80;

    // buttons2 masks.
    private const byte Ps = 0x01;
    private const byte TouchpadClick = 0x02;

    private const float TouchMaxX = 1919f;
    private const float TouchMaxY = 1079f;

    /// <summary>
    /// Auto-detect the report format (USB full / Bluetooth full / compatibility) and parse it. This is the
    /// entry point the HID transport uses; it doesn't need to know which mode the controller is in.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> report, long timestampTicks, out ControllerStateFrame frame)
    {
        frame = default;
        if (report.Length < 2)
        {
            return false;
        }

        // Bluetooth full: id 0x31, one lead byte, then the full body.
        if (report[0] == BluetoothFullReportId && report.Length >= 2 + FullBodyLength)
        {
            frame = ParseFull(report[2..], timestampTicks);
            return true;
        }

        if (report[0] == UsbReportId)
        {
            // USB delivers the full body at exactly 64 bytes; anything else on id 0x01 is the compact layout
            // (Bluetooth compatibility mode delivers it padded to a longer length).
            if (report.Length == UsbReportLength)
            {
                frame = ParseFull(report[1..], timestampTicks);
                return true;
            }

            if (report.Length >= 1 + CompactBodyLength)
            {
                frame = ParseCompact(report[1..], timestampTicks);
                return true;
            }
        }

        return false;
    }

    /// <summary>Parse a USB full report (id 0x01, 64 bytes). Kept for explicit callers/tests.</summary>
    public static bool TryParseUsb(ReadOnlySpan<byte> report, long timestampTicks, out ControllerStateFrame frame)
    {
        frame = default;
        if (report.Length < UsbReportLength || report[0] != UsbReportId)
        {
            return false;
        }

        frame = ParseFull(report[1..], timestampTicks);
        return true;
    }

    /// <summary>Parse a Bluetooth full report (id 0x31): one lead byte then the full body (+ trailing CRC).</summary>
    public static bool TryParseBluetoothFull(ReadOnlySpan<byte> report, long timestampTicks, out ControllerStateFrame frame)
    {
        frame = default;
        if (report.Length < 2 + FullBodyLength || report[0] != BluetoothFullReportId)
        {
            return false;
        }

        frame = ParseFull(report[2..], timestampTicks);
        return true;
    }

    /// <summary>Parse a compatibility (DS4-style compact) report body — sticks/buttons/triggers, no touchpad/IMU.</summary>
    public static bool TryParseCompact(ReadOnlySpan<byte> report, long timestampTicks, out ControllerStateFrame frame)
    {
        frame = default;
        if (report.Length < 1 + CompactBodyLength || report[0] != UsbReportId)
        {
            return false;
        }

        frame = ParseCompact(report[1..], timestampTicks);
        return true;
    }

    private static ControllerStateFrame ParseFull(ReadOnlySpan<byte> body, long timestampTicks)
    {
        ControllerButtons buttons = DecodeButtons(body[Buttons0], body[Buttons1], body[Buttons2]);

        // First touch point: bit 7 of the id byte is set when NOT touching; 12-bit x and y follow.
        byte touchId = body[Touch0];
        bool touching = (touchId & 0x80) == 0;
        int tx = body[Touch0 + 1] | ((body[Touch0 + 2] & 0x0f) << 8);
        int ty = (body[Touch0 + 2] >> 4) | (body[Touch0 + 3] << 4);
        var touchpad = new TouchpadSample(tx / TouchMaxX, ty / TouchMaxY, touching);

        return Build(body[LeftStickX], body[LeftStickY], body[RightStickX], body[RightStickY],
            body[LeftTrigger], body[RightTrigger], buttons, touchpad, timestampTicks);
    }

    private static ControllerStateFrame ParseCompact(ReadOnlySpan<byte> body, long timestampTicks)
    {
        ControllerButtons buttons = DecodeButtons(body[CompactButtons0], body[CompactButtons1], body[CompactButtons2]);

        // Compatibility reports carry no touchpad position; report "not touching" at the origin.
        var touchpad = new TouchpadSample(0f, 0f, false);

        return Build(body[LeftStickX], body[LeftStickY], body[RightStickX], body[RightStickY],
            body[CompactLeftTrigger], body[CompactRightTrigger], buttons, touchpad, timestampTicks);
    }

    private static ControllerStateFrame Build(
        byte lx, byte ly, byte rx, byte ry, byte l2, byte r2,
        ControllerButtons buttons, TouchpadSample touchpad, long timestampTicks)
        => new(
            TimestampTicks: timestampTicks,
            Buttons: buttons,
            LeftStickX: Axis(lx),
            LeftStickY: -Axis(ly),   // raw up = 0; neutral convention is up = +1
            RightStickX: Axis(rx),
            RightStickY: -Axis(ry),
            LeftTrigger: l2 / 255f,
            RightTrigger: r2 / 255f,
            Gyro: null,
            Accel: null,
            Touchpad: touchpad);

    private static ControllerButtons DecodeButtons(byte b0, byte b1, byte b2)
    {
        ControllerButtons buttons = DecodeHat(b0 & 0x0f);
        if ((b0 & Cross) != 0) buttons |= ControllerButtons.South;
        if ((b0 & Circle) != 0) buttons |= ControllerButtons.East;
        if ((b0 & Square) != 0) buttons |= ControllerButtons.West;
        if ((b0 & Triangle) != 0) buttons |= ControllerButtons.North;
        if ((b1 & L1) != 0) buttons |= ControllerButtons.LeftShoulder;
        if ((b1 & R1) != 0) buttons |= ControllerButtons.RightShoulder;
        if ((b1 & L3) != 0) buttons |= ControllerButtons.LeftStick;
        if ((b1 & R3) != 0) buttons |= ControllerButtons.RightStick;
        if ((b1 & Options) != 0) buttons |= ControllerButtons.Start;
        if ((b1 & Create) != 0) buttons |= ControllerButtons.Select;
        if ((b2 & Ps) != 0) buttons |= ControllerButtons.Guide;
        if ((b2 & TouchpadClick) != 0) buttons |= ControllerButtons.TouchpadClick;
        return buttons;
    }

    /// <summary>Map an 8-direction hat (0=up, clockwise; 8=neutral) to the D-pad button flags.</summary>
    private static ControllerButtons DecodeHat(int hat) => hat switch
    {
        0 => ControllerButtons.DPadUp,
        1 => ControllerButtons.DPadUp | ControllerButtons.DPadRight,
        2 => ControllerButtons.DPadRight,
        3 => ControllerButtons.DPadDown | ControllerButtons.DPadRight,
        4 => ControllerButtons.DPadDown,
        5 => ControllerButtons.DPadDown | ControllerButtons.DPadLeft,
        6 => ControllerButtons.DPadLeft,
        7 => ControllerButtons.DPadUp | ControllerButtons.DPadLeft,
        _ => ControllerButtons.None, // 8 (and anything else) = released
    };

    /// <summary>Map an unsigned 0..255 axis (128 center) to [-1, 1] with right/positive.</summary>
    private static float Axis(byte raw) => Math.Clamp((raw - 128) / 127f, -1f, 1f);
}
