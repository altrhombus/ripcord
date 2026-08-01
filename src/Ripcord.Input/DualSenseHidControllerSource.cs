using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Ripcord.Core.Input;
using Ripcord.Input.Common;
using Ripcord.Input.Hid;

namespace Ripcord.Input;

/// <summary>
/// Reads a DualSense directly over raw HID and republishes it as neutral <see cref="ControllerStateFrame"/>s.
/// This is the enhancement path that recovers what Windows.Gaming.Input hides — the PS button, touchpad and
/// real analog triggers — parsed by the platform-neutral <see cref="DualSenseReportParser"/>. A background
/// thread finds the device, reads reports until it disconnects, then polls for it to return (hotplug).
///
/// <para>Phase A is input only; haptics / adaptive triggers / lightbar (output) come later and need a
/// console→client haptics path that doesn't exist yet — hence <see cref="GetExtendedFeatures"/> returns null.
/// Frames are stamped with <see cref="DateTime.UtcNow"/> ticks so the feedback sender's keepalive timing is in
/// the units it expects.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DualSenseHidControllerSource : IControllerSource, IDisposable
{
    public string SourceName => "DualSense raw HID";

    private const ushort SonyVendorId = 0x054C;

    // DualSense (0x0CE6) and DualSense Edge (0x0DF2). Both use report id 0x01 over USB and 0x31 over Bluetooth.
    private static readonly ushort[] DualSensePids = [0x0CE6, 0x0DF2];

    private const string ControllerId = "dualsense-hid-0";

    // replayLast: connection state is STATE, not an event. The poll loop starts in the constructor, so a pad
    // already attached is announced before anything has subscribed — and, being edge-triggered, never again.
    private readonly SimpleObservable<ControllerConnectionEvent> _connections = new(replayLast: true);
    private readonly SimpleObservable<ControllerStateFrame> _stateChanges = new();
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile SafeFileHandle? _handle;

    public DualSenseHidControllerSource()
    {
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "DualSense HID Reader" };
        _thread.Start();
    }

    public IObservable<ControllerConnectionEvent> Connections => _connections;

    public IObservable<ControllerStateFrame> StateChanges(string controllerId) => _stateChanges;

    public IHapticsAndExtendedFeatures? GetExtendedFeatures(string controllerId) => null;

    /// <summary>Whether a DualSense is currently connected — used by the source selector to prefer this path.</summary>
    public static bool IsPresent() => HidInterop.FindFirst(SonyVendorId, DualSensePids) is not null;

    public void Dispose()
    {
        _stop = true;
        _handle?.Dispose(); // unblock a pending ReadFile
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    private void RunLoop()
    {
        bool connected = false;
        try
        {
            while (!_stop)
            {
                HidInterop.HidMatch? match = HidInterop.FindFirst(SonyVendorId, DualSensePids);
                if (match is null || !HidInterop.TryOpen(match.Value.Path, out SafeFileHandle handle))
                {
                    connected = SetConnected(connected, false);
                    Thread.Sleep(500); // poll for the device to (re)appear
                    continue;
                }

                _handle = handle;
                HidInterop.TryActivateDualSenseExtendedReports(handle); // upgrade Bluetooth to the full report
                ReadUntilDisconnect(handle, match.Value.InputReportByteLength, ref connected);
                _handle = null;
                handle.Dispose();
                connected = SetConnected(connected, false);
            }
        }
        catch (Exception)
        {
            // A reader-thread crash must never take the app down; just stop publishing.
        }
        finally
        {
            SetConnected(connected, false);
        }
    }

    private void ReadUntilDisconnect(SafeFileHandle handle, ushort inputReportLength, ref bool connected)
    {
        // Size the buffer to the device's report length (USB 64, Bluetooth 78); fall back to 78 if unknown.
        var buffer = new byte[inputReportLength > 0 ? inputReportLength : 78];
        connected = SetConnected(connected, true, TransportFor(inputReportLength));

        while (!_stop)
        {
            int read = HidInterop.ReadReport(handle, buffer);
            if (read <= 0)
            {
                break; // disconnected, error, or handle closed on shutdown
            }

            var report = buffer.AsSpan(0, read);
            if (DualSenseReportParser.TryParse(report, DateTime.UtcNow.Ticks, out ControllerStateFrame frame))
            {
                _stateChanges.Publish(frame);
            }
        }
    }

    private bool SetConnected(bool current, bool value, ControllerTransport transport = ControllerTransport.Unknown)
    {
        if (current != value)
        {
            _connections.Publish(new ControllerConnectionEvent(ControllerId, value, transport));
        }

        return value;
    }

    private static ControllerTransport TransportFor(ushort inputReportLength) => inputReportLength switch
    {
        64 => ControllerTransport.Usb,
        78 => ControllerTransport.Bluetooth,
        _ => ControllerTransport.Unknown,
    };
}
