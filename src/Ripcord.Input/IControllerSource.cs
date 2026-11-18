using Ripcord.Core.Input;

namespace Ripcord.Input;

public enum ControllerTransport
{
    Usb,
    Bluetooth,
    Unknown,
}

/// <summary>
/// A controller appearing or disappearing. <paramref name="Source"/> names the engine that saw it, filled in by
/// <see cref="CompositeControllerSource"/> — with several engines running at once, "which pad" is only half the
/// answer, since the same physical device can be visible to more than one.
/// </summary>
public sealed record ControllerConnectionEvent(
    string ControllerId,
    bool Connected,
    ControllerTransport Transport,
    string Source = "");

/// <summary>
/// Extended features (adaptive triggers, haptics, lightbar, mic mute LED) only available for
/// advanced vendor gamepads over a supported transport via our own raw-HID engine - never exposed
/// through GameInput/XInput.
/// </summary>
public interface IHapticsAndExtendedFeatures
{
    void SetRumble(float lowFrequency, float highFrequency);

    void SetLightbarColor(byte r, byte g, byte b);

    void SetAdaptiveTrigger(bool leftTrigger, AdaptiveTriggerEffect effect);
}

public enum AdaptiveTriggerEffect
{
    Off,
    Feedback,
    Weapon,
    Vibration,
}

/// <summary>
/// A controller input engine: connection events, neutral state frames, and extended features where the
/// engine has them. The same physical controller can be visible to more than one engine at once (a
/// DualSense through raw HID and through GameInput); <see cref="CompositeControllerSource"/> runs every
/// engine and merges their frames.
/// </summary>
public interface IControllerSource
{
    /// <summary>
    /// Which engine is driving input, for diagnostics. Two sources with different capabilities can serve the same
    /// physical pad, so "a DualSense works" and "GameInput works" are different results — and without this the
    /// overlay could not tell them apart, which makes a hardware test inconclusive.
    /// </summary>
    string SourceName { get; }

    IObservable<ControllerConnectionEvent> Connections { get; }

    IObservable<ControllerStateFrame> StateChanges(string controllerId);

    IHapticsAndExtendedFeatures? GetExtendedFeatures(string controllerId);
}
