using Ripcord.Core.Input;
using Ripcord.Input.Interop;

namespace Ripcord.Input;

/// <summary>
/// Polls the native GamepadReader (Ripcord.Input.Interop) for every connected GameInput gamepad and republishes
/// them as one neutral ControllerStateFrame per poll, standard gamepad button layout.
///
/// <para>
/// <b>Every pad is read from its own device.</b> The reader used to call
/// <c>GetCurrentReading(GameInputKindGamepad, nullptr, …)</c>, and <c>nullptr</c> asks for the most recent reading
/// from ANY gamepad. With a DualSense attached — which GameInput also enumerates, and which reports continuously
/// over Bluetooth — an Xbox pad was starved completely: not one of its presses arrived (measured 2026-08-06), and
/// it had to be re-plugged before readings resumed even after the DualSense went. The reader now tracks devices as
/// they connect and reads each one by name, and <see cref="GamepadSet"/> merges them.
/// </para>
///
/// <para>
/// A DualSense therefore reaches the composite twice, through this engine and through raw HID. That was already
/// true of the old read whenever the DualSense won it, and the composite's merge makes it a no-op.
/// </para>
/// </summary>
public sealed class GameInputControllerSource : IControllerSource, IDisposable
{
    public string SourceName => "GameInput";

    private const string GamepadControllerIdPrefix = "gameinput-gamepad-";
    private const uint MenuBit = 0x00000001;
    private const uint ViewBit = 0x00000002;
    private const uint ABit = 0x00000004;
    private const uint BBit = 0x00000008;
    private const uint XBit = 0x00000010;
    private const uint YBit = 0x00000020;
    private const uint DPadUpBit = 0x00000040;
    private const uint DPadDownBit = 0x00000080;
    private const uint DPadLeftBit = 0x00000100;
    private const uint DPadRightBit = 0x00000200;
    private const uint LeftShoulderBit = 0x00000400;
    private const uint RightShoulderBit = 0x00000800;
    private const uint LeftThumbstickBit = 0x00001000;
    private const uint RightThumbstickBit = 0x00002000;

    private readonly GamepadReader _reader = new();
    // replayLast: connection state is STATE, not an event. The poll loop starts in the constructor, so a pad
    // already attached is announced before anything has subscribed — and, being edge-triggered, never again.
    private readonly SimpleObservable<ControllerConnectionEvent> _connections = new(replayLast: true);
    private readonly SimpleObservable<ControllerStateFrame> _stateChanges = new();
    private readonly Timer _pollTimer;
    private readonly GamepadSet _pads = new();

    // A timer callback can start while the previous one is still running, and GamepadSet is single-threaded by
    // design. An overlapping tick is skipped rather than queued: the next one reads current state anyway.
    private int _polling;

    // Each pad's id as the diagnostics show it, "gameinput-045e:0b13-2" for vendor, product and reader id.
    // Kept per device because a disconnect is reported after the device has stopped appearing in the poll.
    private readonly Dictionary<ulong, string> _labels = [];

    public GameInputControllerSource()
    {
        _pollTimer = new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(8));
    }

    public IObservable<ControllerConnectionEvent> Connections => _connections;

    public IObservable<ControllerStateFrame> StateChanges(string controllerId) => _stateChanges;

    public IHapticsAndExtendedFeatures? GetExtendedFeatures(string controllerId) => null;

    public void Dispose() => _pollTimer.Dispose();

    private void Poll(object? state)
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            var states = _reader.GetConnectedStates();
            foreach (GamepadState pad in states)
            {
                _labels[pad.DeviceId] = $"{GamepadControllerIdPrefix}{pad.VendorId:x4}:{pad.ProductId:x4}-{pad.DeviceId}";
            }

            var readings = states.Select(pad => new GamepadReading(pad.DeviceId, ToFrame(pad))).ToList();

            GamepadSetUpdate update = _pads.Update(readings);

            foreach (ulong id in update.Disconnected)
            {
                _connections.Publish(new ControllerConnectionEvent(Label(id), false, ControllerTransport.Unknown));
                _labels.Remove(id);
            }

            foreach (ulong id in update.Connected)
            {
                _connections.Publish(new ControllerConnectionEvent(Label(id), true, ControllerTransport.Unknown));
            }

            if (update.Merged is { } merged)
            {
                _stateChanges.Publish(merged);
            }
            else if (update.Disconnected.Count > 0)
            {
                // The last pad just went. The composite keeps each engine's latest frame, so without a neutral
                // one here whatever was held at the moment of unplugging would stay held.
                _stateChanges.Publish(default(ControllerStateFrame) with { TimestampTicks = DateTime.UtcNow.Ticks });
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private string Label(ulong id) => _labels.TryGetValue(id, out string? label) ? label : GamepadControllerIdPrefix + id;

    private static ControllerStateFrame ToFrame(GamepadState pad) => new(
        TimestampTicks: unchecked((long)pad.TimestampTicks),
        Buttons: MapButtons(pad.Buttons),
        LeftStickX: pad.LeftThumbstickX,
        LeftStickY: pad.LeftThumbstickY,
        RightStickX: pad.RightThumbstickX,
        RightStickY: pad.RightThumbstickY,
        LeftTrigger: pad.LeftTrigger,
        RightTrigger: pad.RightTrigger,
        Gyro: null,
        Accel: null,
        Touchpad: null);

    private static ControllerButtons MapButtons(ulong raw)
    {
        var result = ControllerButtons.None;
        if ((raw & ABit) != 0) result |= ControllerButtons.South;
        if ((raw & BBit) != 0) result |= ControllerButtons.East;
        if ((raw & XBit) != 0) result |= ControllerButtons.West;
        if ((raw & YBit) != 0) result |= ControllerButtons.North;
        if ((raw & DPadUpBit) != 0) result |= ControllerButtons.DPadUp;
        if ((raw & DPadDownBit) != 0) result |= ControllerButtons.DPadDown;
        if ((raw & DPadLeftBit) != 0) result |= ControllerButtons.DPadLeft;
        if ((raw & DPadRightBit) != 0) result |= ControllerButtons.DPadRight;
        if ((raw & LeftShoulderBit) != 0) result |= ControllerButtons.LeftShoulder;
        if ((raw & RightShoulderBit) != 0) result |= ControllerButtons.RightShoulder;
        if ((raw & LeftThumbstickBit) != 0) result |= ControllerButtons.LeftStick;
        if ((raw & RightThumbstickBit) != 0) result |= ControllerButtons.RightStick;
        if ((raw & MenuBit) != 0) result |= ControllerButtons.Start;
        if ((raw & ViewBit) != 0) result |= ControllerButtons.Select;
        return result;
    }
}
