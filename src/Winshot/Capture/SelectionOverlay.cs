using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Winshot.Capture;

/// <summary>
/// Borderless topmost window covering one monitor, showing the frozen capture dimmed,
/// with the dragged selection shown at full brightness.
/// </summary>
internal sealed class SelectionOverlay : Form
{
    private const int WM_DPICHANGED = 0x02E0;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int MinSelection = 3;

    private static readonly Font LabelFont = new("Segoe UI", 9f);

    private readonly Rectangle _monitorBounds;
    private readonly Bitmap _bright;
    private readonly Bitmap _dim;
    private readonly string? _hint;
    private readonly Font? _hintFont;
    private Point? _dragStart;
    private Rectangle _selection;

    public SelectionOverlay(ScreenSnapshot snapshot, Rectangle monitorBounds, string? hint)
    {
        _monitorBounds = monitorBounds;
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

        if (_selection.Width <= 0 || _selection.Height <= 0)
            return;

        g.DrawImage(_bright, _selection, _selection, GraphicsUnit.Pixel);

        g.CompositingMode = CompositingMode.SourceOver;
        g.DrawRectangle(Pens.White, _selection.X, _selection.Y, _selection.Width - 1, _selection.Height - 1);

        string label = $"{_selection.Width} × {_selection.Height}";
        var size = TextRenderer.MeasureText(label, LabelFont);
        var labelRect = new Rectangle(_selection.Right - size.Width - 4, _selection.Bottom + 4, size.Width + 4, size.Height + 2);
        if (labelRect.Bottom > ClientSize.Height)
            labelRect.Y = _selection.Bottom - labelRect.Height - 4;
        using (var bg = new SolidBrush(Color.FromArgb(200, 32, 32, 32)))
            g.FillRectangle(bg, labelRect);
        TextRenderer.DrawText(g, label, LabelFont, labelRect, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
            return;

        var rect = Rectangle.FromLTRB(
            Math.Min(start.X, e.X), Math.Min(start.Y, e.Y),
            Math.Max(start.X, e.X), Math.Max(start.Y, e.Y));
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
