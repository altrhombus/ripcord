using Ripcord.Core.Input;
using Ripcord.Input.Interop;

namespace Ripcord.Input;

/// <summary>
/// Polls the native GamepadReader (Ripcord.Input.Interop) for the first connected GameInput
/// gamepad and republishes it as neutral ControllerStateFrames. Phase 0 scope only: a single
/// polled device, standard gamepad button layout. Multi-device de-duplication (advanced pads seen
/// through both raw-HID and any gamepad-emulation layer) and advanced vendor extended features
/// arrive with the raw-HID engine in a later phase, per the plan.
/// </summary>
public sealed class GameInputControllerSource : IControllerSource, IDisposable
{
    public string SourceName => "GameInput";

    private const string GamepadControllerId = "gameinput-gamepad-0";
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
    private bool _wasConnected;

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
        var gamepad = _reader.GetLatestState();

        if (gamepad.IsConnected != _wasConnected)
        {
            _wasConnected = gamepad.IsConnected;
            _connections.Publish(new ControllerConnectionEvent(GamepadControllerId, gamepad.IsConnected, ControllerTransport.Unknown));
        }

        if (!gamepad.IsConnected)
        {
            return;
        }

        _stateChanges.Publish(new ControllerStateFrame(
            TimestampTicks: unchecked((long)gamepad.TimestampTicks),
            Buttons: MapButtons(gamepad.Buttons),
            LeftStickX: gamepad.LeftThumbstickX,
            LeftStickY: gamepad.LeftThumbstickY,
            RightStickX: gamepad.RightThumbstickX,
            RightStickY: gamepad.RightThumbstickY,
            LeftTrigger: gamepad.LeftTrigger,
            RightTrigger: gamepad.RightTrigger,
            Gyro: null,
            Accel: null,
            Touchpad: null));
    }

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
