using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Windshot.Capture;

/// <summary>What's shown on screen during a live capture (scrolling capture, recording), kept out of the capture itself.</summary>
internal static class CaptureChrome
{
    internal const int WS_EX_TOOLWINDOW = 0x80;
    internal const int WS_EX_TRANSPARENT = 0x20;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOPMOST = 0x8;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>Keeps a window out of screen captures, so it never ends up in a frame even if it overlaps the area.</summary>
    public static void ExcludeFromCapture(Form form)
    {
        if (!SetWindowDisplayAffinity(form.Handle, WDA_EXCLUDEFROMCAPTURE))
            Log.Write($"Couldn't exclude {form.GetType().Name} from capture (error {Marshal.GetLastWin32Error()})");
    }
}

/// <summary>A click-through outline just outside the area being captured.</summary>
internal sealed class AreaFrame : Form
{
    private static readonly Color Key = Color.FromArgb(255, 0, 255);
    private readonly Color _outline;
    private bool _paused;

    /// <summary>While paused, the outline turns amber.</summary>
    public void ShowPaused(bool paused)
    {
        _paused = paused;
        Invalidate();
    }

    private readonly int _thickness;

    public AreaFrame(Rectangle area, double scale, Color outline)
    {
        _outline = outline;
        _thickness = Math.Max(2, (int)Math.Round(2 * scale));
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Key;
        TransparencyKey = Key;
        var bounds = area;
        bounds.Inflate(_thickness, _thickness);
        Bounds = bounds;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= CaptureChrome.WS_EX_TOOLWINDOW | CaptureChrome.WS_EX_TRANSPARENT | CaptureChrome.WS_EX_NOACTIVATE | CaptureChrome.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureChrome.ExcludeFromCapture(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(_paused ? Color.FromArgb(240, 180, 40) : _outline, _thickness) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }
}

/// <summary>
/// A small dark bar beside the captured area, with a status line and buttons. It doesn't take
/// focus, so the keyboard stays with the app being captured.
/// </summary>
internal abstract class FloatingPanel : Form
{
    protected static readonly Color Accent = Color.FromArgb(64, 132, 214);
    protected static readonly Color Plain = Color.FromArgb(64, 64, 64);
    protected readonly Font PanelFont;
    private readonly double _scale;

    protected FloatingPanel(double scale)
    {
        _scale = scale;
        PanelFont = new Font("Segoe UI", (float)(14 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = Color.FromArgb(32, 32, 32);
        Font = PanelFont;
    }

    protected int S(double dip) => (int)Math.Round(dip * _scale);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= CaptureChrome.WS_EX_TOOLWINDOW | CaptureChrome.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureChrome.ExcludeFromCapture(this);
    }

    /// <summary>Sizes the bar with rounded ends and places it by the area.</summary>
    protected void Layout(Rectangle area, Size size)
    {
        Size = size;
        Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, S(12), S(12)));
        Location = Place(area, size, S(10));
    }

    protected Button MakeButton(string text, Color color, Rectangle bounds)
    {
        var button = new Button
        {
            Text = text,
            Bounds = bounds,
            FlatStyle = FlatStyle.Flat,
            BackColor = color,
            ForeColor = Color.White,
            Font = PanelFont,
            TabStop = false,
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    /// <summary>Below the area if there's room on its monitor, else above, else inside along the bottom.</summary>
    private static Point Place(Rectangle area, Size size, int gap)
    {
        var monitor = Screen.FromRectangle(area).WorkingArea;
        int x = Math.Clamp(area.X + (area.Width - size.Width) / 2, monitor.Left, Math.Max(monitor.Left, monitor.Right - size.Width));
        if (area.Bottom + gap + size.Height <= monitor.Bottom)
            return new Point(x, area.Bottom + gap);
        if (area.Top - gap - size.Height >= monitor.Top)
            return new Point(x, area.Top - gap - size.Height);
        return new Point(x, Math.Min(area.Bottom, monitor.Bottom) - gap - size.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            PanelFont.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
}

/// <summary>The countdown before recording: a big number in the middle of the area.</summary>
internal sealed class CountdownBadge : Form
{
    private readonly Font _font;
    private string _text = "";

    public CountdownBadge(Rectangle area, double scale)
    {
        int side = (int)Math.Round(132 * scale);
        _font = new Font("Segoe UI Semibold", (float)(72 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(32, 32, 32);
        Opacity = 0.88;
        Bounds = new Rectangle(area.X + (area.Width - side) / 2, area.Y + (area.Height - side) / 2, side, side);
        var shape = CreateRoundRectRgn(0, 0, side + 1, side + 1, side / 3, side / 3);
        Region = Region.FromHrgn(shape);
        DeleteObject(shape);
    }

    public void ShowNumber(int n)
    {
        _text = n.ToString();
        if (!Visible)
            Show();
        Invalidate();
        Update();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= CaptureChrome.WS_EX_TOOLWINDOW | CaptureChrome.WS_EX_TRANSPARENT | CaptureChrome.WS_EX_NOACTIVATE | CaptureChrome.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureChrome.ExcludeFromCapture(this);
    }

    protected override void OnPaint(PaintEventArgs e) =>
        TextRenderer.DrawText(e.Graphics, _text, _font, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _font.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
