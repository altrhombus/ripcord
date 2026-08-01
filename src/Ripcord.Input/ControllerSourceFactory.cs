using System.Runtime.Versioning;

namespace Ripcord.Input;

/// <summary>
/// Chooses the controller source for a session. Prefers the DualSense raw-HID path when a DualSense is
/// connected (it recovers the PS button, touchpad and real triggers that Windows.Gaming.Input hides);
/// otherwise uses the GameInput baseline, which covers standard pads (Xbox, the ROG Ally X's built-in
/// controller, 8BitDo in XInput mode) and a DualSense's basics.
///
/// <para>Picking a single source also sidesteps double-consuming one physical pad through two APIs. Choosing
/// per mixed-fleet at runtime (e.g. a DualSense and an Xbox pad plugged in together, or hot-swapping mid
/// session) is a later refinement; this resolves once, at session start.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ControllerSourceFactory
{
    /// <summary>
    /// Every engine, running together, rather than one picked at start-up.
    ///
    /// <para>
    /// Choosing was the bug: the decision was made once when the session page loaded, so swapping a DualSense for
    /// an Xbox pad mid-session was never noticed, and with a DualSense attached an Xbox pad could not be used at
    /// all. DualSenseHidControllerSource is constructed unconditionally — IsPresent() is deliberately NOT consulted
    /// — because it already polls for its device to appear, so it costs one idle thread and gains hot-plug.
    /// </para>
    /// </summary>
    public static IControllerSource Create()
        => new CompositeControllerSource(
            new DualSenseHidControllerSource(),
            new GameInputControllerSource());
}
