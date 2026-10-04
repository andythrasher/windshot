using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Windshot.Capture;

/// <summary>How Windows 11 draws a window's corners.</summary>
internal enum WindowCorners
{
    Square,
    /// <summary>The usual 8 DIP radius.</summary>
    Round,
    /// <summary>4 DIPs, which windows can ask for.</summary>
    RoundSmall,
}

/// <summary>A window the user can click to capture, in physical desktop pixels.</summary>
/// <param name="IsDesktop">The desktop itself; clicking it captures the whole monitor instead.</param>
internal readonly record struct WindowRegion(Rectangle Bounds, bool IsDesktop, WindowCorners Corners = WindowCorners.Square)
{
    /// <summary>The corner radius in physical pixels on a monitor at this scale.</summary>
    public int CornerRadius(double scale) => (int)Math.Round(Corners switch
    {
        WindowCorners.Round => 8 * scale,
        WindowCorners.RoundSmall => 4 * scale,
        _ => 0,
    });
}

/// <summary>Lists the windows on screen, front to back, at the moment of capture.</summary>
internal static class WindowFinder
{
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0xC00000;
    private const int WS_THICKFRAME = 0x40000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DEFAULT = 0, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;

    /// <summary>Rounded window corners arrived with Windows 11.</summary>
    private static readonly bool RoundedCornersExist = Environment.OSVersion.Version.Build >= 22000;

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
        return new WindowRegion(bounds, isDesktop, isDesktop ? WindowCorners.Square : CornersOf(hwnd));
    }

    /// <summary>
    /// Windows 11 rounds framed windows unless they're maximized or snapped, or asked not to
    /// be; a window can also ask for rounding (or small rounding) explicitly.
    /// </summary>
    private static WindowCorners CornersOf(IntPtr hwnd)
    {
        if (!RoundedCornersExist || IsZoomed(hwnd) || IsArranged(hwnd))
            return WindowCorners.Square;
        if (DwmGetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out int preference, sizeof(int)) != 0)
            preference = DWMWCP_DEFAULT;
        return preference switch
        {
            DWMWCP_DONOTROUND => WindowCorners.Square,
            DWMWCP_ROUND => WindowCorners.Round,
            DWMWCP_ROUNDSMALL => WindowCorners.RoundSmall,
            // By default only framed windows are rounded, not bare popups.
            _ => (GetWindowLong(hwnd, GWL_STYLE) & (WS_CAPTION | WS_THICKFRAME)) != 0 ? WindowCorners.Round : WindowCorners.Square,
        };
    }

    /// <summary>Snapped to an edge or a snap layout zone, where Windows squares the corners.</summary>
    private static bool IsArranged(IntPtr hwnd)
    {
        try
        {
            return IsWindowArranged(hwnd);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
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
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowArranged(IntPtr hwnd);

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
