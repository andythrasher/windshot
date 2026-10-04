using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Windshot.Capture;

/// <summary>A window the user can click to capture, in physical desktop pixels.</summary>
/// <param name="IsDesktop">The desktop itself; clicking it captures the whole monitor instead.</param>
internal readonly record struct WindowRegion(Rectangle Bounds, bool IsDesktop);

/// <summary>Lists the windows on screen, front to back, at the moment of capture.</summary>
internal static class WindowFinder
{
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int GWL_EXSTYLE = -20;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    public static List<WindowRegion> VisibleWindows()
    {
        var windows = new List<WindowRegion>();
        EnumWindows((hwnd, _) =>
        {
            if (TryDescribe(hwnd) is WindowRegion region)
                windows.Add(region);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static WindowRegion? TryDescribe(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            return null;
        // Click-through windows (game and GPU overlays) would otherwise swallow every click.
        if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0)
            return null;
        // Cloaked windows are "visible" but not shown: suspended store apps, other virtual desktops.
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return null;

        // The extended frame excludes the invisible resize borders and the drop shadow,
        // so the capture is exactly the window you see.
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(hwnd, out frame))
            return null;
        var bounds = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
        if (bounds.Width < 2 || bounds.Height < 2)
            return null;

        var className = new StringBuilder(64);
        GetClassName(hwnd, className, className.Capacity);
        bool isDesktop = className.ToString() is "Progman" or "WorkerW";
        return new WindowRegion(bounds, isDesktop);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
