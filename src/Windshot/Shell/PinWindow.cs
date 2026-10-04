using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windshot.Capture;

namespace Windshot.Shell;

/// <summary>
/// A capture floating above other windows. Drag to move, scroll to zoom around the cursor,
/// Ctrl+scroll for opacity, Esc or middle-click to close, right-click for more.
/// </summary>
internal sealed class PinWindow : Form
{
    private const int CS_DROPSHADOW = 0x20000;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const float MinZoom = 0.1f;
    private const float MaxZoom = 8f;

    private static readonly List<PinWindow> Open = new();

    private readonly Bitmap _image;
    private readonly double _scale;
    private readonly Action<CapturedImage> _edit;
    private float _zoom = 1;
    private Point? _grab;

    /// <param name="location">Where the image's top-left goes, in physical desktop pixels.</param>
    /// <param name="scale">Device pixels per DIP of the source capture, kept for editing later.</param>
    public PinWindow(Bitmap image, Point location, double scale, Action<CapturedImage> edit)
    {
        _image = image;
        _scale = scale;
        _edit = edit;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Bounds = new Rectangle(location, image.Size);
        ContextMenuStrip = BuildMenu();
        Open.Add(this);
    }

    public static void CloseAll()
    {
        foreach (var pin in Open.ToList())
            pin.Close();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;      // subtle shadow so it reads as floating
            cp.ExStyle |= WS_EX_TOOLWINDOW;      // keep out of Alt+Tab
            return cp;
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy", null, (_, _) => Clipboard.SetImage(_image));
        menu.Items.Add("Edit", null, (_, _) => EditAndClose());
        var opacity = new ToolStripMenuItem("Opacity");
        foreach (int percent in new[] { 100, 75, 50, 25 })
            opacity.DropDownItems.Add($"{percent}%", null, (_, _) => Opacity = percent / 100.0);
        menu.Items.Add(opacity);
        menu.Items.Add("Reset zoom", null, (_, _) => SetZoom(1, new Point(0, 0)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Close", null, (_, _) => Close());
        return menu;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // Crisp pixels at 100% and when zoomed in; smooth when shrunk.
        g.InterpolationMode = _zoom >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_image, ClientRectangle);

        using var border = new Pen(Color.FromArgb(70, 0, 0, 0));
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            _grab = e.Location;
        else if (e.Button == MouseButtons.Middle)
            Close();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_grab is Point grab)
            Location = new Point(Cursor.Position.X - grab.X, Cursor.Position.Y - grab.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e) => _grab = null;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int steps = Math.Sign(e.Delta);
        if (ModifierKeys.HasFlag(Keys.Control))
            Opacity = Math.Clamp(Opacity + steps * 0.1, 0.2, 1);
        else
            SetZoom(_zoom * MathF.Pow(1.1f, steps), e.Location);
    }

    /// <summary>Zooms while keeping the image point under <paramref name="anchor"/> (client coordinates) in place.</summary>
    private void SetZoom(float zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        // Snap to exactly 100% when passing through it, so pixels can line up again.
        if ((_zoom < 1 && zoom > 1) || (_zoom > 1 && zoom < 1))
            zoom = 1;

        float fx = ClientSize.Width == 0 ? 0 : (float)anchor.X / ClientSize.Width;
        float fy = ClientSize.Height == 0 ? 0 : (float)anchor.Y / ClientSize.Height;
        var size = new Size(Math.Max(1, (int)(_image.Width * zoom)), Math.Max(1, (int)(_image.Height * zoom)));
        var screenAnchor = PointToScreen(anchor);
        Bounds = new Rectangle(
            screenAnchor.X - (int)(fx * size.Width),
            screenAnchor.Y - (int)(fy * size.Height),
            size.Width, size.Height);
        _zoom = zoom;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
            Close();
        else if (e.Control && e.KeyCode == Keys.C)
            Clipboard.SetImage(_image);
    }

    private void EditAndClose()
    {
        // Annotations are already flattened into the pin, so the editor gets a single image.
        var data = _image.LockBits(new Rectangle(Point.Empty, _image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var pixels = new byte[_image.Width * _image.Height * 4];
        try
        {
            for (int y = 0; y < _image.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * _image.Width * 4, _image.Width * 4);
        }
        finally
        {
            _image.UnlockBits(data);
        }

        _edit(new CapturedImage(pixels, _image.Width, _image.Height, _scale, new Rectangle(Location, _image.Size)));
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Closing a modeless form disposes it, which also releases the image.
        Open.Remove(this);
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _image.Dispose();
        base.Dispose(disposing);
    }
}
