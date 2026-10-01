using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ripcord_App.Services;

/// <summary>
/// Puts the window's XAML content flush with the top of the client area in full screen.
///
/// <para>
/// A window that extends its content into the title bar has its content host, a child window, placed one pixel
/// down from the top of the client area, leaving a strip for the resize border. In full screen there is no border,
/// but the host stays where it was and the strip shows the top-level window's own background: white in light
/// theme, a line across the top of the display. Seen on hardware on 2026-10-01: the host at y=1 on a client area
/// at y=0, and moving the host to y=0 from outside the process removed the line with nothing moving it back.
/// Neither the DWM border colour nor turning off <c>ExtendsContentIntoTitleBar</c> moved it. Why the host keeps
/// the offset in full screen is [X].
/// </para>
/// </summary>
internal static class ContentBridge
{
    private const string BridgeClass = "Microsoft.UI.Content.DesktopChildSiteBridge";

    /// <summary>Size the content host to the whole client area of <paramref name="window"/>, if it has one.</summary>
    public static void PinToClientArea(IntPtr window)
    {
        if (!GetClientRect(window, out Rect client))
        {
            return;
        }

        IntPtr bridge = FindBridge(window);
        if (bridge == IntPtr.Zero || !GetWindowRect(bridge, out Rect current))
        {
            return;
        }

        // Compare in client coordinates, so an already-pinned host is left alone.
        var origin = new Point { X = current.Left, Y = current.Top };
        ScreenToClient(window, ref origin);
        if (origin.X == 0 && origin.Y == 0
            && current.Right - current.Left == client.Right && current.Bottom - current.Top == client.Bottom)
        {
            return;
        }

        SetWindowPos(bridge, IntPtr.Zero, 0, 0, client.Right, client.Bottom, NoZOrder | NoActivate);
    }

    private static IntPtr FindBridge(IntPtr window)
    {
        IntPtr found = IntPtr.Zero;
        var name = new StringBuilder(64);
        EnumChildWindows(window, (child, _) =>
        {
            name.Clear();
            GetClassName(child, name, name.Capacity);
            if (name.ToString() == BridgeClass && GetParent(child) == window)
            {
                found = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    // DllImport rather than LibraryImport, for the reason AppEffects gives.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
