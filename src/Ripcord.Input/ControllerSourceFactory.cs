using System.Runtime.Versioning;

namespace Ripcord.Input;

/// <summary>
/// Builds the app's controller source: a composite of every input engine. The DualSense raw-HID engine
/// recovers the PS button, touchpad and real triggers that Windows.Gaming.Input hides; the GameInput engine
/// covers standard pads (Xbox, the ROG Ally X's built-in controller, 8BitDo in XInput mode) and a DualSense's
/// basics.
///
/// <para>A DualSense is read by both engines at once, and the composite's merge makes the duplicate a
/// no-op.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ControllerSourceFactory
{
    /// <summary>
    /// Every engine, running together, rather than one picked at start-up.
    ///
    /// <para>
    /// A choice made once would not notice a DualSense swapped for an Xbox pad afterwards, and with a DualSense
    /// attached an Xbox pad could not be used at all.
    /// DualSenseHidControllerSource is constructed unconditionally — IsPresent() is deliberately NOT consulted
    /// — because it already polls for its device to appear, so it costs one idle thread and gains hot-plug.
    /// </para>
    /// </summary>
    public static IControllerSource Create()
        => new CompositeControllerSource(
            new DualSenseHidControllerSource(),
            new GameInputControllerSource());
}
