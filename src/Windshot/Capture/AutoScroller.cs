using System.Drawing;
using System.Runtime.InteropServices;

namespace Windshot.Capture;

internal enum AutoScrollState
{
    Running,
    /// <summary>The content stopped moving after scrolling: the end of the page.</summary>
    AtEnd,
    /// <summary>Nothing moved at all, either way of scrolling.</summary>
    Stuck,
    /// <summary>The user moved the mouse while it was parked over the area.</summary>
    Interrupted,
}

/// <summary>
/// Scrolls the area a notch at a time for a scrolling capture, driven by what the stitcher
/// sees. First it posts wheel messages to the window under the area, which leaves the
/// mouse alone; apps that ignore those (some only listen to real input) get real wheel
/// input instead, with the mouse parked over the area and put back afterwards.
/// </summary>
internal sealed class AutoScroller : IDisposable
{
    private const int StepMs = 200;
    /// <summary>Notches without any movement before trying the next way of scrolling.</summary>
    private const int TriesPerMethod = 4;
    /// <summary>How long the content must stay still, after it has moved, to count as the end.</summary>
    private const int EndQuietMs = 1500;
    private const int WheelNotch = 120;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private readonly Point _target;
    private bool _realInput;
    private Point? _savedCursor;
    private int _steps;
    private bool _progressed;
    private long _lastStep = long.MinValue / 2;
    private long _lastProgress;

    /// <param name="area">The area being captured, in desktop pixels.</param>
    /// <param name="avoid">A rectangle not to aim at (the panel, if it had to go inside the area).</param>
    public AutoScroller(Rectangle area, Rectangle avoid)
    {
        var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
        _target = avoid.Contains(center) ? new Point(center.X, area.Y + area.Height / 4) : center;
    }

    /// <summary>Call once per frame with what the stitcher made of it.</summary>
    /// <param name="now">A millisecond clock.</param>
    public AutoScrollState Tick(StitchResult result, long now)
    {
        if (result == StitchResult.Appended)
        {
            _progressed = true;
            _lastProgress = now;
        }

        if (_realInput && GetCursorPos(out var cursor) && (cursor.X != _target.X || cursor.Y != _target.Y))
        {
            _savedCursor = null; // they've taken the mouse; leave it where they put it
            return AutoScrollState.Interrupted;
        }

        if (now - _lastStep < StepMs)
            return AutoScrollState.Running;

        if (_progressed && now - _lastProgress > EndQuietMs)
            return AutoScrollState.AtEnd;
        if (!_progressed && _steps >= TriesPerMethod)
        {
            if (_realInput)
                return AutoScrollState.Stuck;
            Log.Write("Auto-scroll: posted wheel messages had no effect; using real wheel input");
            _realInput = true;
            _steps = 0;
            if (GetCursorPos(out var saved))
                _savedCursor = new Point(saved.X, saved.Y);
        }

        // Scrolled too far to stitch: back up a notch so the frames overlap again.
        Wheel(result == StitchResult.Lost ? WheelNotch : -WheelNotch);
        _steps++;
        _lastStep = now;
        return AutoScrollState.Running;
    }

    private void Wheel(int delta)
    {
        if (_realInput)
        {
            SetCursorPos(_target.X, _target.Y);
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, delta, UIntPtr.Zero);
            return;
        }

        // The deepest window under the area; windows that don't handle the wheel pass it to their parent.
        var window = WindowFromPoint(new POINT { X = _target.X, Y = _target.Y });
        if (window == IntPtr.Zero)
            return;
        var wParam = (IntPtr)((delta & 0xFFFF) << 16);
        var lParam = (IntPtr)(((_target.Y & 0xFFFF) << 16) | (_target.X & 0xFFFF));
        PostMessage(window, WM_MOUSEWHEEL, wParam, lParam);
    }

    /// <summary>Puts the mouse back where it was, if it was borrowed.</summary>
    public void Dispose()
    {
        if (_savedCursor is Point p)
            SetCursorPos(p.X, p.Y);
        _savedCursor = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extraInfo);
}
