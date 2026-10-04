using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Windshot.Capture;

/// <summary>
/// Borderless topmost window covering one monitor, showing the frozen capture dimmed.
/// Hovering lights up the window under the cursor and a click captures it; dragging lights
/// up and captures a region instead.
/// </summary>
internal sealed class SelectionOverlay : Form
{
    private const int WM_DPICHANGED = 0x02E0;
    private const int WS_EX_TOOLWINDOW = 0x80;
    /// <summary>Smaller drags count as a click (on a window), so a little hand wobble is fine.</summary>
    private const int MinSelection = 5;

    private static readonly Font LabelFont = new("Segoe UI", 9f);
    private static readonly Color WindowHighlight = Color.FromArgb(64, 132, 214);

    private readonly Rectangle _monitorBounds;
    private readonly Rectangle _virtualBounds;
    private readonly IReadOnlyList<WindowRegion> _windows;
    private readonly Bitmap _bright;
    private readonly Bitmap _dim;
    private readonly string? _hint;
    private readonly Font? _hintFont;
    private Point? _dragStart;
    private Rectangle _selection;
    /// <summary>The window under the cursor, in desktop coordinates; what a click captures.</summary>
    private Rectangle? _hover;

    /// <param name="windows">Windows on screen at the moment of capture, front to back.</param>
    public SelectionOverlay(ScreenSnapshot snapshot, Rectangle monitorBounds, IReadOnlyList<WindowRegion> windows, string? hint)
    {
        _monitorBounds = monitorBounds;
        _virtualBounds = snapshot.VirtualBounds;
        _windows = windows;
        MonitorScale = GetMonitorScale(monitorBounds);
        _hint = hint;
        if (hint is not null)
            _hintFont = new Font("Segoe UI", (float)(15 * MonitorScale), FontStyle.Regular, GraphicsUnit.Pixel);

        // Pre-render both layers once so painting during a drag is just two blits.
        _bright = snapshot.Bitmap.Clone(snapshot.ToLocal(monitorBounds), PixelFormat.Format32bppPArgb);
        _dim = new Bitmap(_bright);
        using (var g = Graphics.FromImage(_dim))
        using (var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            g.FillRectangle(shade, 0, 0, _dim.Width, _dim.Height);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Bounds = monitorBounds;
    }

    public event Action<SelectionOverlay, Rectangle>? Selected;
    public event Action? Cancelled;

    /// <summary>Device pixels per DIP on this monitor.</summary>
    public double MonitorScale { get; }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW; // keep out of Alt+Tab
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Bounds are already in this monitor's physical pixels; never let Windows rescale us.
        if (m.Msg == WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Everything is painted in OnPaint.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;

        g.DrawImage(_dim, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
        DrawHint(g);

        if (_selection.Width > 0 && _selection.Height > 0)
            DrawLit(g, _selection, _selection.Size, Color.White, 1);
        else if (_dragStart is null && HoverClientRect() is Rectangle window)
            DrawLit(g, window, _hover!.Value.Size, WindowHighlight, (int)Math.Ceiling(2 * MonitorScale));
    }

    /// <summary>Shows an area at full brightness, outlined, with its size (which may be larger than the visible part).</summary>
    private void DrawLit(Graphics g, Rectangle area, Size size, Color border, int thickness)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(_bright, area, area, GraphicsUnit.Pixel);

        g.CompositingMode = CompositingMode.SourceOver;
        using (var pen = new Pen(border, thickness) { Alignment = PenAlignment.Inset })
            g.DrawRectangle(pen, area.X, area.Y, area.Width - 1, area.Height - 1);

        string label = $"{size.Width} × {size.Height}";
        var text = TextRenderer.MeasureText(label, LabelFont);
        var labelRect = new Rectangle(area.Right - text.Width - 4, area.Bottom + 4, text.Width + 4, text.Height + 2);
        if (labelRect.Bottom > ClientSize.Height)
            labelRect.Y = area.Bottom - labelRect.Height - 4;
        using (var bg = new SolidBrush(Color.FromArgb(200, 32, 32, 32)))
            g.FillRectangle(bg, labelRect);
        TextRenderer.DrawText(g, label, LabelFont, labelRect, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>The visible part of the hovered window on this monitor, in client coordinates.</summary>
    private Rectangle? HoverClientRect()
    {
        if (_hover is not Rectangle hover)
            return null;
        hover.Offset(-_monitorBounds.X, -_monitorBounds.Y);
        hover.Intersect(ClientRectangle);
        return hover.IsEmpty ? null : hover;
    }

    /// <summary>What a click at this point captures: the frontmost window there, or the monitor for the desktop.</summary>
    private Rectangle WindowAt(Point desktop)
    {
        foreach (var window in _windows)
        {
            if (window.Bounds.Contains(desktop))
                return window.IsDesktop ? _monitorBounds : Rectangle.Intersect(window.Bounds, _virtualBounds);
        }
        return _monitorBounds;
    }

    private void UpdateHover(Point client)
    {
        var desktop = new Point(client.X + _monitorBounds.X, client.Y + _monitorBounds.Y);
        var target = WindowAt(desktop);
        if (_hover == target)
            return;

        InvalidateHover();
        _hover = target;
        InvalidateHover();
    }

    private void InvalidateHover()
    {
        if (HoverClientRect() is Rectangle area)
        {
            area.Inflate(4, 40); // room for the border and size label
            Invalidate(area);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Light up whatever is under the cursor right away, before it moves.
        if (_monitorBounds.Contains(Cursor.Position))
            UpdateHover(PointToClient(Cursor.Position));
    }

    private void DrawHint(Graphics g)
    {
        if (_hint is null || _hintFont is null)
            return;

        var text = TextRenderer.MeasureText(_hint, _hintFont);
        int padX = (int)(16 * MonitorScale), padY = (int)(8 * MonitorScale);
        var rect = new Rectangle((ClientSize.Width - text.Width) / 2 - padX, (int)(24 * MonitorScale),
            text.Width + padX * 2, text.Height + padY * 2);

        var old = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceOver;
        using (var bg = new SolidBrush(Color.FromArgb(220, 32, 32, 32)))
            g.FillRectangle(bg, rect);
        TextRenderer.DrawText(g, _hint, _hintFont, rect, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        g.CompositingMode = old;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            Cancelled?.Invoke();
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            _dragStart = e.Location;
            SetSelection(Rectangle.Empty);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not Point start)
        {
            UpdateHover(e.Location);
            return;
        }

        var rect = Rectangle.FromLTRB(
            Math.Min(start.X, e.X), Math.Min(start.Y, e.Y),
            Math.Max(start.X, e.X), Math.Max(start.Y, e.Y));
        // Once it's clearly a drag, the window highlight gives way to the region.
        if (_selection.IsEmpty && rect.Width >= MinSelection && rect.Height >= MinSelection)
            InvalidateHover();
        SetSelection(Rectangle.Intersect(rect, ClientRectangle));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _dragStart is null)
            return;

        _dragStart = null;
        if (_selection.Width >= MinSelection && _selection.Height >= MinSelection)
        {
            var desktopRect = _selection;
            desktopRect.Offset(_monitorBounds.Location);
            Selected?.Invoke(this, desktopRect);
        }
        else if (_hover is Rectangle window)
        {
            // A click: capture the window under the cursor.
            Selected?.Invoke(this, window);
        }
        else
        {
            SetSelection(Rectangle.Empty);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
            Cancelled?.Invoke();
    }

    private void SetSelection(Rectangle rect)
    {
        // Repaint only what changed: old and new selection plus room for the size label.
        var dirty = Rectangle.Union(_selection, rect);
        dirty.Inflate(2, 40);
        _selection = rect;
        Invalidate(dirty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bright.Dispose();
            _dim.Dispose();
            _hintFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static double GetMonitorScale(Rectangle bounds)
    {
        var center = new POINT { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
        var monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
