using System.Drawing;
using System.Runtime.InteropServices;

namespace Windshot;

/// <summary>
/// The monitors as they are right now. WinForms' Screen.AllScreens caches its list and only
/// refreshes it on a SystemEvents notification that never arrives in this app, so a monitor
/// connected after startup would be missing from captures.
/// </summary>
internal static class Monitors
{
    internal readonly record struct Monitor(Rectangle Bounds, Rectangle WorkingArea, bool IsPrimary);

    public static IReadOnlyList<Monitor> All()
    {
        var monitors = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(handle, ref info))
                monitors.Add(new Monitor(info.rcMonitor.ToRectangle(), info.rcWork.ToRectangle(), (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    public static Monitor? Primary => All().FirstOrDefault(m => m.IsPrimary);

    private const uint MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
