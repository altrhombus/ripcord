using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Ripcord.Input.Hid;

/// <summary>
/// Minimal user-mode HID access (SetupAPI enumeration + CreateFile/ReadFile) for reading a specific
/// controller's raw input reports — the path Windows.Gaming.Input can't give us (it hides the DualSense's PS
/// button, touchpad, etc.). Same technique as DS4Windows/hidapi. Windows-only.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HidInterop
{
    private static readonly Guid HidClassGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;

    public readonly record struct HidMatch(string Path, ushort InputReportByteLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    // HIDP_CAPS is 64 bytes (5 leading USHORTs, then Reserved[17] + 10 trailing count fields). We only need
    // the first few, so declare those and reserve the full size — no unsafe/fixed buffer required.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet, nint deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        nint deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        nint deviceInterfaceDetailData, uint detailDataSize, out uint requiredSize, nint deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(nint preparsedData, ref HIDP_CAPS capabilities);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint bytesToRead, out uint bytesRead, nint overlapped);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, uint bufferLength);

    /// <summary>
    /// Best-effort nudge for a DualSense to start sending its full (0x31) report over Bluetooth: reading
    /// feature report 0x05 (calibration) switches it out of the default DS4-compatibility mode into extended
    /// reports. Harmless over USB (already full) and if it fails the compatibility report still parses.
    /// </summary>
    public static void TryActivateDualSenseExtendedReports(SafeFileHandle handle)
    {
        try
        {
            var feature = new byte[64];
            feature[0] = 0x05;
            HidD_GetFeature(handle, feature, (uint)feature.Length);
        }
        catch
        {
            // best-effort only
        }
    }

    /// <summary>Find the first present HID device matching the vendor id and any of the product ids.</summary>
    public static HidMatch? FindFirst(ushort vendorId, ReadOnlySpan<ushort> productIds)
    {
        Guid guid = HidClassGuid;
        nint set = SetupDiGetClassDevs(ref guid, 0, 0, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == -1)
        {
            return null;
        }

        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref iface); i++)
            {
                string? path = GetDevicePath(set, ref iface);
                if (path is null || !TryOpen(path, out SafeFileHandle handle))
                {
                    continue;
                }

                using (handle)
                {
                    var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(handle, ref attrs) || attrs.VendorID != vendorId)
                    {
                        continue;
                    }

                    if (!productIds.Contains(attrs.ProductID))
                    {
                        continue;
                    }

                    return new HidMatch(path, GetInputReportLength(handle));
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return null;
    }

    public static bool TryOpen(string path, out SafeFileHandle handle)
    {
        handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            // Retry read-only: output access (needed later for haptics) may be denied while input isn't.
            handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return false;
            }
        }

        return true;
    }

    /// <summary>Blocking read of one input report. Returns bytes read, or -1 on failure / closed handle.</summary>
    public static int ReadReport(SafeFileHandle handle, byte[] buffer)
    {
        try
        {
            return ReadFile(handle, buffer, (uint)buffer.Length, out uint read, 0) ? (int)read : -1;
        }
        catch (ObjectDisposedException)
        {
            return -1; // handle closed to unblock us during shutdown
        }
    }

    private static string? GetDevicePath(nint set, ref SP_DEVICE_INTERFACE_DATA iface)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref iface, 0, 0, out uint required, 0);
        if (required == 0)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA: cbSize (8 on 64-bit, 6 on 32-bit) then the inline path at offset 4.
            Marshal.WriteInt32(buffer, Environment.Is64BitProcess ? 8 : 6);
            return SetupDiGetDeviceInterfaceDetail(set, ref iface, buffer, required, out _, 0)
                ? Marshal.PtrToStringUni(buffer + 4)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ushort GetInputReportLength(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out nint preparsed) || preparsed == 0)
        {
            return 0;
        }

        try
        {
            var caps = new HIDP_CAPS();
            return HidP_GetCaps(preparsed, ref caps) >= 0 ? caps.InputReportByteLength : (ushort)0;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }
}
