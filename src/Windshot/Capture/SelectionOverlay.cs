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
    /// <summary>The window behind <see cref="_hover"/>, unless that's a whole monitor.</summary>
    private WindowRegion? _hoverWindow;

    private static readonly Color RulerColor = Color.FromArgb(255, 45, 120);

    /// <summary>The cursor in client coordinates, while it's over this monitor.</summary>
    private Point? _cursor;
    private bool _showLoupe = Settings.Current.Capture.ShowMagnifier;
    private bool _rulerOn;
    private PixelRuler? _ruler;
    private Measurement? _measurement;
    /// <summary>Area currently covered by the loupe and ruler, so it can be repainted when they move.</summary>
    private Rectangle _aidsArea;

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

    /// <summary>An area was picked, in desktop pixels; with the window, when a window was clicked.</summary>
    public event Action<SelectionOverlay, Rectangle, WindowRegion?>? Selected;
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

        if (CurrentLit() is Lit lit)
            DrawLit(g, lit);

        if (_cursor is Point cursor)
        {
            if (_rulerOn && _dragStart is null && _measurement is Measurement m)
                DrawRuler(g, m);
            if (_showLoupe)
                Loupe.Draw(g, _bright, cursor, Loupe.Bounds(cursor, ClientSize, MonitorScale), MonitorScale,
                    ColorAt(cursor), ToDesktop(cursor));
        }
    }

    // ---- Magnifier, color picker and ruler ---------------------------------------------

    private Point ToDesktop(Point client) => new(client.X + _monitorBounds.X, client.Y + _monitorBounds.Y);

    private Color ColorAt(Point client)
    {
        var c = _bright.GetPixel(Math.Clamp(client.X, 0, _bright.Width - 1), Math.Clamp(client.Y, 0, _bright.Height - 1));
        return Color.FromArgb(255, c.R, c.G, c.B);
    }

    private void DrawRuler(Graphics g, Measurement m)
    {
        float thickness = (float)Math.Max(1, Math.Round(MonitorScale));
        int tick = (int)(5 * MonitorScale);
        var state = g.Save();
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.None;
        using (var pen = new Pen(RulerColor, thickness))
        {
            // Lines span the same-colored run, edge to edge, with end ticks.
            int y = m.Origin.Y, x = m.Origin.X;
            g.DrawLine(pen, m.Left, y, m.Right, y);
            g.DrawLine(pen, m.Left, y - tick, m.Left, y + tick);
            g.DrawLine(pen, m.Right, y - tick, m.Right, y + tick);
            g.DrawLine(pen, x, m.Top, x, m.Bottom);
            g.DrawLine(pen, x - tick, m.Top, x + tick, m.Top);
            g.DrawLine(pen, x - tick, m.Bottom, x + tick, m.Bottom);
        }

        var label = RulerLabelBounds(m, out string text);
        using (var bg = new SolidBrush(RulerColor))
            g.FillRectangle(bg, label);
        TextRenderer.DrawText(g, text, LabelFont, label, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        g.Restore(state);
    }

    /// <summary>The measurement label, above-left of the cursor so it doesn't collide with the loupe.</summary>
    private Rectangle RulerLabelBounds(Measurement m, out string text)
    {
        text = $"{m.Width} × {m.Height}";
        // At display scaling other than 100%, also give the size in 100%-scale pixels (CSS/design units).
        if (Math.Abs(MonitorScale - 1) > 0.01)
            text += $"   ({Math.Round(m.Width / MonitorScale)} × {Math.Round(m.Height / MonitorScale)} at 100%)";
        var size = TextRenderer.MeasureText(text, LabelFont);
        int gap = (int)(12 * MonitorScale);
        var rect = new Rectangle(m.Origin.X - gap - size.Width - 8, m.Origin.Y - gap - size.Height - 4, size.Width + 8, size.Height + 4);
        if (rect.X < 0) rect.X = m.Origin.X + gap;
        if (rect.Y < 0) rect.Y = m.Origin.Y + gap;
        return rect;
    }

    /// <summary>Moves the loupe and ruler to the cursor, repainting only where they were and are.</summary>
    private void UpdateCursorAids(Point? cursor)
    {
        _cursor = cursor;
        _measurement = null;
        var area = Rectangle.Empty;
        if (cursor is Point p)
        {
            if (_showLoupe)
                area = Loupe.Bounds(p, ClientSize, MonitorScale);
            if (_rulerOn)
            {
                _ruler ??= new PixelRuler(_bright);
                var m = _ruler.Measure(p);
                _measurement = m;
                int tick = (int)(6 * MonitorScale) + 2;
                area = Union(area, Rectangle.FromLTRB(m.Left - 2, m.Origin.Y - tick, m.Right + 3, m.Origin.Y + tick + 1));
                area = Union(area, Rectangle.FromLTRB(m.Origin.X - tick, m.Top - 2, m.Origin.X + tick + 1, m.Bottom + 3));
                area = Union(area, RulerLabelBounds(m, out _));
            }
        }
        Invalidate(_aidsArea);
        Invalidate(area);
        _aidsArea = area;
    }

    private static Rectangle Union(Rectangle a, Rectangle b) => a.IsEmpty ? b : Rectangle.Union(a, b);

    /// <summary>C: copy the color under the cursor and finish.</summary>
    private void CopyColor()
    {
        if (_cursor is not Point p)
            return;
        var c = ColorAt(p);
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        Finish(Shell.ClipboardWriter.TrySetText(hex) ? $"Copied {hex}" : "Couldn't copy: the clipboard is busy", p);
    }

    /// <summary>Click in ruler mode: copy the measurement and finish.</summary>
    private void CopyMeasurement()
    {
        if (_measurement is not Measurement m)
            return;
        string text = $"{m.Width} × {m.Height}";
        Finish(Shell.ClipboardWriter.TrySetText(text) ? $"Copied {text}" : "Couldn't copy: the clipboard is busy", m.Origin);
    }

    private void Finish(string message, Point client)
    {
        var desktop = ToDesktop(client);
        Shell.Hud.Show(message, new Rectangle(desktop.X - 1, desktop.Y - 1, 2, 2), MonitorScale);
        Cancelled?.Invoke(); // nothing to capture; just close the overlay
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateCursorAids(null); // the cursor moved to another monitor's overlay
    }

    /// <summary>An area shown at full brightness, outlined, with its size (which may be larger than the visible part).</summary>
    private readonly record struct Lit(Rectangle Area, Size Size, Color Border, int Thickness);

    /// <summary>What's lit right now: the selection while dragging, else the window under the cursor.</summary>
    private Lit? CurrentLit()
    {
        // Until the mouse has clearly moved, a press is still a click on the hovered window.
        bool dragging = _selection.Width >= MinSelection || _selection.Height >= MinSelection;
        if (dragging)
            return _selection.Width > 0 && _selection.Height > 0 ? new Lit(_selection, _selection.Size, Color.White, 1) : null;
        if (!_rulerOn && HoverClientRect() is Rectangle window)
            return new Lit(window, _hover!.Value.Size, WindowHighlight, (int)Math.Ceiling(2 * MonitorScale));
        return null;
    }

    /// <summary>
    /// Everything the lit area paints, label included. Painting and invalidation both go
    /// through here so they can't disagree; when they did, stale outlines and labels stayed
    /// on screen (e.g. a label wider than a narrow selection).
    /// </summary>
    private Rectangle LitBounds(Lit lit) => Rectangle.Union(lit.Area, LabelBounds(lit, out _));

    private Rectangle LabelBounds(Lit lit, out string label)
    {
        label = $"{lit.Size.Width} × {lit.Size.Height}";
        var text = TextRenderer.MeasureText(label, LabelFont);
        var rect = new Rectangle(lit.Area.Right - text.Width - 4, lit.Area.Bottom + 4, text.Width + 4, text.Height + 2);
        if (rect.Bottom > ClientSize.Height)
            rect.Y = lit.Area.Bottom - rect.Height - 4;
        return rect;
    }

    /// <summary>The lit area as last invalidated, so it can be erased when it changes.</summary>
    private Lit? _litShown;

    /// <summary>Call after anything that changes <see cref="CurrentLit"/>: repaints where it was and where it is.</summary>
    private void RefreshLit()
    {
        var lit = CurrentLit();
        if (lit == _litShown)
            return;
        if (_litShown is Lit old)
            Invalidate(LitBounds(old));
        if (lit is Lit now)
            Invalidate(LitBounds(now));
        _litShown = lit;
    }

    private void DrawLit(Graphics g, Lit lit)
    {
        var area = lit.Area;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(_bright, area, area, GraphicsUnit.Pixel);

        g.CompositingMode = CompositingMode.SourceOver;
        using (var pen = new Pen(lit.Border, lit.Thickness) { Alignment = PenAlignment.Inset })
            g.DrawRectangle(pen, area.X, area.Y, area.Width - 1, area.Height - 1);

        var labelRect = LabelBounds(lit, out string label);
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
    /// <returns>The area, and the window when it's a window (not the monitor).</returns>
    private (Rectangle Area, WindowRegion? Window) WindowAt(Point desktop)
    {
        foreach (var window in _windows)
        {
            if (window.Bounds.Contains(desktop))
                return window.IsDesktop ? (_monitorBounds, null) : (Rectangle.Intersect(window.Bounds, _virtualBounds), window);
        }
        return (_monitorBounds, null);
    }

    private void UpdateHover(Point client)
    {
        var desktop = new Point(client.X + _monitorBounds.X, client.Y + _monitorBounds.Y);
        (_hover, _hoverWindow) = WindowAt(desktop);
        RefreshLit();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Light up whatever is under the cursor right away, before it moves.
        if (_monitorBounds.Contains(Cursor.Position))
        {
            UpdateHover(PointToClient(Cursor.Position));
            UpdateCursorAids(PointToClient(Cursor.Position));
        }
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
        UpdateCursorAids(e.Location);
        if (_dragStart is not Point start)
        {
            UpdateHover(e.Location);
            return;
        }

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
            Selected?.Invoke(this, desktopRect, null);
        }
        else if (_rulerOn)
        {
            CopyMeasurement();
        }
        else if (_hover is Rectangle window)
        {
            // A click: capture the window under the cursor.
            Selected?.Invoke(this, window, _hoverWindow);
        }
        else
        {
            SetSelection(Rectangle.Empty);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Cancelled?.Invoke();
                break;
            case Keys.C when e.Modifiers == Keys.None:
                CopyColor();
                break;
            case Keys.R when e.Modifiers == Keys.None:
                _rulerOn = !_rulerOn;
                RefreshLit(); // the window highlight gives way to the ruler, and back
                UpdateCursorAids(_cursor);
                break;
            case Keys.M when e.Modifiers == Keys.None:
                _showLoupe = !_showLoupe;
                UpdateCursorAids(_cursor);
                break;
            // Arrow keys nudge the cursor a pixel at a time, for precise picking and selecting.
            case Keys.Left: Cursor.Position = Cursor.Position with { X = Cursor.Position.X - 1 }; break;
            case Keys.Right: Cursor.Position = Cursor.Position with { X = Cursor.Position.X + 1 }; break;
            case Keys.Up: Cursor.Position = Cursor.Position with { Y = Cursor.Position.Y - 1 }; break;
            case Keys.Down: Cursor.Position = Cursor.Position with { Y = Cursor.Position.Y + 1 }; break;
        }
    }

    // Arrow keys are normally used for focus navigation; claim them for nudging instead.
    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    private void SetSelection(Rectangle rect)
    {
        _selection = rect;
        RefreshLit();
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
