using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Windshot.Shell;

/// <summary>
/// A small dark pill that briefly shows a message (like "Copied 12 words") centered on a
/// screen area, then disappears. It never takes focus and clicks pass through it.
/// </summary>
internal sealed class Hud : Form
{
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x8;

    private static Hud? _current;

    private readonly string _message;
    private readonly Font _font;

    private Hud(string message, Rectangle area, double scale)
    {
        _message = message;
        _font = new Font("Segoe UI", (float)(15 * scale), FontStyle.Regular, GraphicsUnit.Pixel);

        var text = TextRenderer.MeasureText(message, _font);
        var size = new Size(text.Width + (int)(32 * scale), text.Height + (int)(18 * scale));

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(32, 32, 32);
        Bounds = new Rectangle(
            area.X + (area.Width - size.Width) / 2,
            area.Y + (area.Height - size.Height) / 2,
            size.Width, size.Height);
        Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, size.Height, size.Height));
    }

    /// <summary>Shows a message centered on a desktop-space area (physical pixels).</summary>
    public static void Show(string message, Rectangle area, double scale)
    {
        _current?.Close();
        var hud = new Hud(message, area, scale);
        _current = hud;
        hud.Show();

        var timer = new System.Windows.Forms.Timer { Interval = 1600 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            hud.Close();
            if (_current == hud)
                _current = null;
        };
        timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e) =>
        TextRenderer.DrawText(e.Graphics, _message, _font, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _font.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
}
