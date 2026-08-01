using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ripcord.HidCapture;

/// <summary>
/// Thin P/Invoke over SetupAPI + hid.dll to enumerate HID devices and read their input reports. This is the
/// classic user-mode HID path (CreateFile on the device interface + ReadFile), the same one DS4Windows /
/// hidapi use — game-controller top-level collections are readable this way even though the WinRT HidDevice
/// API blocks them. Windows-only at run time; compiles anywhere (DllImports resolve on Windows).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HidNative
{
    private static readonly Guid HidClassGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;

    public readonly record struct HidDeviceInfo(
        string Path, ushort VendorId, ushort ProductId, string Product,
        ushort UsagePage, ushort Usage, ushort InputReportByteLength);

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

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[27]; // Reserved[17] + 10 trailing count fields
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

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, uint bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint bytesToRead, out uint bytesRead, nint overlapped);

    /// <summary>Enumerate every present HID device interface with its attributes/caps (best-effort per device).</summary>
    public static List<HidDeviceInfo> Enumerate()
    {
        var results = new List<HidDeviceInfo>();
        Guid guid = HidClassGuid;
        nint set = SetupDiGetClassDevs(ref guid, 0, 0, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == -1)
        {
            return results;
        }

        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref iface); i++)
            {
                string? path = GetDevicePath(set, ref iface);
                if (path is null)
                {
                    continue;
                }

                if (TryOpen(path, out SafeFileHandle handle))
                {
                    using (handle)
                    {
                        results.Add(Describe(path, handle));
                    }
                }
                else
                {
                    // A device we can't open (in use / access denied) still gets listed with what we know.
                    results.Add(new HidDeviceInfo(path, 0, 0, "(cannot open)", 0, 0, 0));
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return results;
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
            // SP_DEVICE_INTERFACE_DETAIL_DATA: cbSize (8 on 64-bit due to alignment, 6 on 32-bit) then the
            // inline device-path string, which begins at byte offset 4.
            Marshal.WriteInt32(buffer, Environment.Is64BitProcess ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, buffer, required, out _, 0))
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool TryOpen(string path, out SafeFileHandle handle)
    {
        handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        return true;
    }

    private static HidDeviceInfo Describe(string path, SafeFileHandle handle)
    {
        var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        HidD_GetAttributes(handle, ref attrs);

        ushort usage = 0, usagePage = 0, inputLen = 0;
        if (HidD_GetPreparsedData(handle, out nint preparsed) && preparsed != 0)
        {
            try
            {
                var caps = new HIDP_CAPS();
                if (HidP_GetCaps(preparsed, ref caps) >= 0) // HIDP_STATUS_SUCCESS = 0x00110000; >=0 is fine
                {
                    usage = caps.Usage;
                    usagePage = caps.UsagePage;
                    inputLen = caps.InputReportByteLength;
                }
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        return new HidDeviceInfo(path, attrs.VendorID, attrs.ProductID, GetProduct(handle), usagePage, usage, inputLen);
    }

    private static string GetProduct(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        if (HidD_GetProductString(handle, buffer, (uint)buffer.Length))
        {
            string s = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            return s.Length == 0 ? "(no name)" : s;
        }

        return "(no name)";
    }

    /// <summary>Blocking read of one input report. Returns the number of bytes read, or -1 on failure/closed handle.</summary>
    public static int ReadReport(SafeFileHandle handle, byte[] buffer)
        => ReadFile(handle, buffer, (uint)buffer.Length, out uint read, 0) ? (int)read : -1;
}
