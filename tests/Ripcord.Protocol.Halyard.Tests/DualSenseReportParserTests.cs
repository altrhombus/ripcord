using Ripcord.Core.Input;
using Ripcord.Input.Common;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// DualSense report parsing. Reports are synthesized with known field values (rather than pasted from a
/// capture) so the tests encode the reversed layout and no capture bytes land in the repo. Offsets match the
/// live USB capture used to reverse the format: id 0x01, sticks b1..b4, triggers b5/b6, buttons b8..b10,
/// touch point at b33.
/// </summary>
public class DualSenseReportParserTests
{
    /// <summary>Build a 64-byte USB report (id 0x01). Body offset N lands at report index N+1.</summary>
    private static byte[] Usb(
        byte lx = 128, byte ly = 128, byte rx = 128, byte ry = 128,
        byte l2 = 0, byte r2 = 0, byte buttons0 = 0x08, byte buttons1 = 0, byte buttons2 = 0,
        byte touchId = 0x80, int touchX = 0, int touchY = 0)
    {
        var r = new byte[DualSenseReportParser.UsbReportLength];
        r[0] = DualSenseReportParser.UsbReportId;
        r[1] = lx; r[2] = ly; r[3] = rx; r[4] = ry;
        r[5] = l2; r[6] = r2;
        r[8] = buttons0; r[9] = buttons1; r[10] = buttons2;
        r[33] = touchId;
        r[34] = (byte)(touchX & 0xff);
        r[35] = (byte)(((touchX >> 8) & 0x0f) | ((touchY & 0x0f) << 4));
        r[36] = (byte)((touchY >> 4) & 0xff);
        return r;
    }

    [Fact]
    public void RejectsWrongIdOrLength()
    {
        Assert.False(DualSenseReportParser.TryParseUsb(new byte[64], 0, out _));      // id 0x00
        Assert.False(DualSenseReportParser.TryParseUsb(new byte[10] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0, out _)); // too short
    }

    [Fact]
    public void NeutralReportIsAllReleasedAndCentered()
    {
        Assert.True(DualSenseReportParser.TryParseUsb(Usb(), 123, out var f));
        Assert.Equal(123, f.TimestampTicks);
        Assert.Equal(ControllerButtons.None, f.Buttons);
        Assert.Equal(0f, f.LeftStickX, 3);
        Assert.Equal(0f, f.LeftStickY, 3);
        Assert.Equal(0f, f.LeftTrigger, 3);
        Assert.False(f.Touchpad!.Value.Touching);
    }

    [Theory]
    [InlineData((byte)0x20, ControllerButtons.South)]   // Cross
    [InlineData((byte)0x40, ControllerButtons.East)]    // Circle
    [InlineData((byte)0x10, ControllerButtons.West)]    // Square
    [InlineData((byte)0x80, ControllerButtons.North)]   // Triangle
    public void FaceButtonsMap(byte faceBit, ControllerButtons expected)
    {
        // Face bits sit in the high nibble of buttons0; keep the hat at neutral (0x08).
        Assert.True(DualSenseReportParser.TryParseUsb(Usb(buttons0: (byte)(0x08 | faceBit)), 0, out var f));
        Assert.Equal(expected, f.Buttons);
    }

    [Theory]
    [InlineData(0, ControllerButtons.DPadUp)]
    [InlineData(2, ControllerButtons.DPadRight)]
    [InlineData(4, ControllerButtons.DPadDown)]
    [InlineData(6, ControllerButtons.DPadLeft)]
    [InlineData(1, ControllerButtons.DPadUp | ControllerButtons.DPadRight)]
    [InlineData(8, ControllerButtons.None)]
    public void DpadHatMaps(int hat, ControllerButtons expected)
    {
        Assert.True(DualSenseReportParser.TryParseUsb(Usb(buttons0: (byte)hat), 0, out var f));
        Assert.Equal(expected, f.Buttons);
    }

    [Fact]
    public void ShoulderStickAndSystemButtonsMap()
    {
        // buttons1: L1|R1|L3|R3|Create|Options ; buttons2: PS | touchpad-click.
        byte b1 = 0x01 | 0x02 | 0x40 | 0x80 | 0x10 | 0x20;
        byte b2 = 0x01 | 0x02;
        Assert.True(DualSenseReportParser.TryParseUsb(Usb(buttons0: 0x08, buttons1: b1, buttons2: b2), 0, out var f));
        Assert.True(f.Buttons.HasFlag(ControllerButtons.LeftShoulder));
        Assert.True(f.Buttons.HasFlag(ControllerButtons.RightShoulder));
        Assert.True(f.Buttons.HasFlag(ControllerButtons.LeftStick));
        Assert.True(f.Buttons.HasFlag(ControllerButtons.RightStick));
        Assert.True(f.Buttons.HasFlag(ControllerButtons.Select));       // Create
        Assert.True(f.Buttons.HasFlag(ControllerButtons.Start));        // Options
        Assert.True(f.Buttons.HasFlag(ControllerButtons.Guide));        // PS
        Assert.True(f.Buttons.HasFlag(ControllerButtons.TouchpadClick));
    }

    [Fact]
    public void SticksAndTriggersScale()
    {
        // Full left + full up on the left stick, full right + full down on the right, both triggers maxed.
        Assert.True(DualSenseReportParser.TryParseUsb(
            Usb(lx: 0, ly: 0, rx: 255, ry: 255, l2: 255, r2: 128), 0, out var f));
        Assert.Equal(-1f, f.LeftStickX, 2);
        Assert.Equal(1f, f.LeftStickY, 2);   // raw up (0) -> neutral up = +1
        Assert.Equal(1f, f.RightStickX, 2);
        Assert.Equal(-1f, f.RightStickY, 2); // raw down (255) -> -1
        Assert.Equal(1f, f.LeftTrigger, 2);
        Assert.Equal(0.5f, f.RightTrigger, 2);
    }

    [Fact]
    public void TouchpadPositionDecodes()
    {
        // Touching (id bit7 clear), x=1000, y=500.
        Assert.True(DualSenseReportParser.TryParseUsb(Usb(touchId: 0x00, touchX: 1000, touchY: 500), 0, out var f));
        TouchpadSample t = f.Touchpad!.Value;
        Assert.True(t.Touching);
        Assert.Equal(1000f / 1919f, t.X, 3);
        Assert.Equal(500f / 1079f, t.Y, 3);
    }

    [Fact]
    public void BluetoothFullBodyIsUsbBodyShiftedByOne()
    {
        // Build a USB full report, then re-frame its body as a Bluetooth full report (id 0x31 + 1 lead byte)
        // and confirm the same fields parse identically.
        byte[] usb = Usb(lx: 10, buttons0: (byte)(0x08 | 0x20), buttons2: 0x01);
        var bt = new byte[2 + 63];
        bt[0] = DualSenseReportParser.BluetoothFullReportId;
        bt[1] = 0x00; // lead byte
        usb.AsSpan(1).CopyTo(bt.AsSpan(2)); // USB body (after id) becomes BT body (after id + lead)

        Assert.True(DualSenseReportParser.TryParseBluetoothFull(bt, 7, out var f));
        Assert.Equal(7, f.TimestampTicks);
        Assert.True(f.Buttons.HasFlag(ControllerButtons.South));
        Assert.True(f.Buttons.HasFlag(ControllerButtons.Guide));
        Assert.Equal(DualSenseReportParser.TryParseUsb(usb, 7, out var u) ? u.LeftStickX : float.NaN, f.LeftStickX, 4);
    }

    /// <summary>Build a Bluetooth-compatibility (DS4-style compact) report: id 0x01, buttons at b5/b6/b7,
    /// triggers at b8/b9, padded to 78 bytes (how Windows delivers it over BT by default).</summary>
    private static byte[] Compact(
        byte lx = 128, byte ly = 128, byte rx = 128, byte ry = 128,
        byte buttons0 = 0x08, byte buttons1 = 0, byte buttons2 = 0, byte l2 = 0, byte r2 = 0)
    {
        var r = new byte[78];
        r[0] = DualSenseReportParser.UsbReportId;
        r[1] = lx; r[2] = ly; r[3] = rx; r[4] = ry;
        r[5] = buttons0; r[6] = buttons1; r[7] = buttons2; r[8] = l2; r[9] = r2;
        return r;
    }

    [Fact]
    public void CompactParsesButtonsSticksTriggers()
    {
        // Cross (face in b5 high nibble), PS (b7 bit0), full L2.
        Assert.True(DualSenseReportParser.TryParseCompact(
            Compact(lx: 0, buttons0: (byte)(0x08 | 0x20), buttons2: 0x01, l2: 255), 0, out var f));
        Assert.True(f.Buttons.HasFlag(ControllerButtons.South));
        Assert.True(f.Buttons.HasFlag(ControllerButtons.Guide));
        Assert.Equal(-1f, f.LeftStickX, 2);
        Assert.Equal(1f, f.LeftTrigger, 2);
        Assert.False(f.Touchpad!.Value.Touching); // no touchpad position in compatibility mode
    }

    [Fact]
    public void TryParseDispatchesByIdAndLength()
    {
        // USB full: id 0x01, 64 bytes, face in b8.
        Assert.True(DualSenseReportParser.TryParse(Usb(buttons0: (byte)(0x08 | 0x20)), 0, out var usb));
        Assert.True(usb.Buttons.HasFlag(ControllerButtons.South));

        // Bluetooth compatibility: id 0x01, 78 bytes, face in b5.
        Assert.True(DualSenseReportParser.TryParse(Compact(buttons0: (byte)(0x08 | 0x40)), 0, out var compat));
        Assert.True(compat.Buttons.HasFlag(ControllerButtons.East)); // Circle

        // Bluetooth full: id 0x31.
        var bt = new byte[2 + 63];
        bt[0] = DualSenseReportParser.BluetoothFullReportId;
        Usb(buttons0: (byte)(0x08 | 0x80)).AsSpan(1).CopyTo(bt.AsSpan(2));
        Assert.True(DualSenseReportParser.TryParse(bt, 0, out var btFull));
        Assert.True(btFull.Buttons.HasFlag(ControllerButtons.North)); // Triangle
    }
}
