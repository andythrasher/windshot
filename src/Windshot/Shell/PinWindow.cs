using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windshot.Capture;

namespace Windshot.Shell;

/// <summary>
/// A capture floating above other windows. Drag to move, scroll to zoom around the cursor,
/// Ctrl+scroll for opacity, Esc or middle-click to close, right-click for more. Pins outlive
/// restarts (see <see cref="PinStore"/>) until they're closed.
/// </summary>
internal sealed class PinWindow : Form
{
    private const int CS_DROPSHADOW = 0x20000;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_LAYERED = 0x80000;
    private const float MinZoom = 0.1f;
    private const float MaxZoom = 8f;

    private static readonly List<PinWindow> Open = new();

    private readonly Bitmap _image;
    private readonly double _scale;
    /// <summary>Names its files in <see cref="PinStore"/>.</summary>
    private readonly string _id;
    private readonly bool _restored;
    private readonly Action<CapturedImage> _edit;
    /// <summary>Transparent pixels (rounded window corners, unfilled canvas) show what's behind.</summary>
    private readonly bool _opaque;
    private float _zoom = 1;
    private double _opacity = 1;
    private Point? _grab;
    private bool _dragged;

    /// <summary>Set while Windshot quits, so closing pins then keeps them for next time.</summary>
    public static bool AppExiting { get; set; }

    /// <param name="location">Where the image's top-left goes, in physical desktop pixels.</param>
    /// <param name="scale">Device pixels per DIP of the source capture, kept for editing later.</param>
    /// <param name="id">For a pin restored from <see cref="PinStore"/>, with its zoom and opacity; null for a new one.</param>
    public PinWindow(Bitmap image, Point location, double scale, Action<CapturedImage> edit,
        string? id = null, float zoom = 1, double opacity = 1)
    {
        _image = image;
        _scale = scale;
        _edit = edit;
        _opaque = IsOpaque(image);
        _restored = id is not null;
        _id = id ?? Guid.NewGuid().ToString("N");
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _opacity = Math.Clamp(opacity, 0.2, 1);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Bounds = new Rectangle(location, new Size(Math.Max(1, (int)(image.Width * _zoom)), Math.Max(1, (int)(image.Height * _zoom))));
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
            // A subtle shadow so it reads as floating; it's rectangular, so only for rectangular images.
            if (_opaque)
                cp.ClassStyle |= CS_DROPSHADOW;
            // Layered with per-pixel alpha (see Render); kept out of Alt+Tab.
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED;
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
            opacity.DropDownItems.Add($"{percent}%", null, (_, _) => SetOpacity(percent / 100.0));
        menu.Items.Add(opacity);
        menu.Items.Add("Reset zoom", null, (_, _) => SetZoom(1, new Point(0, 0)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Close", null, (_, _) => Close());
        return menu;
    }

    private void SetOpacity(double opacity)
    {
        _opacity = opacity;
        Render();
        SaveState();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Render();
        if (!_restored)
        {
            PinStore.SaveImage(_id, _image);
            SaveState();
        }
    }

    private void SaveState() =>
        PinStore.SaveState(_id, new PinStore.PinState(Left, Top, _zoom, _opacity, _scale));

    /// <summary>
    /// Draws the pin into the layered window. Unlike painting, this keeps the image's own
    /// transparency, so rounded window corners stay round.
    /// </summary>
    private void Render()
    {
        if (!IsHandleCreated)
            return;
        var size = ClientSize;
        using var frame = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(frame))
        {
            // Crisp pixels at 100% and when zoomed in; smooth when shrunk.
            g.InterpolationMode = _zoom >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_image, new Rectangle(Point.Empty, size));
            g.PixelOffsetMode = PixelOffsetMode.None;
            if (_opaque)
            {
                using var border = new Pen(Color.FromArgb(70, 0, 0, 0));
                g.DrawRectangle(border, 0, 0, size.Width - 1, size.Height - 1);
            }
        }

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = frame.GetHbitmap(Color.FromArgb(0)); // keeps alpha, premultiplied
        var previous = SelectObject(memory, bitmap);
        try
        {
            var position = new POINT { X = Left, Y = Top };
            var extent = new SIZE { Width = size.Width, Height = size.Height };
            var origin = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = (byte)Math.Round(_opacity * 255), AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screen, ref position, ref extent, memory, ref origin, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static bool IsOpaque(Bitmap image)
    {
        if (!Image.IsAlphaPixelFormat(image.PixelFormat))
            return true;
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[image.Width];
            for (int y = 0; y < image.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                foreach (int p in row)
                {
                    if ((uint)p >> 24 != 255)
                        return false;
                }
            }
            return true;
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _grab = e.Location;
            _dragged = false;
        }
        else if (e.Button == MouseButtons.Middle)
            Close();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_grab is Point grab)
        {
            Location = new Point(Cursor.Position.X - grab.X, Cursor.Position.Y - grab.Y);
            _dragged = true;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _grab = null;
        if (_dragged)
            SaveState();
        _dragged = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int steps = Math.Sign(e.Delta);
        if (ModifierKeys.HasFlag(Keys.Control))
            SetOpacity(Math.Clamp(_opacity + steps * 0.1, 0.2, 1));
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
        Render();
        SaveState();
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
        // Closed by the user (or turned back into an editor): it's gone for good.
        if (!AppExiting)
            PinStore.Delete(_id);
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _image.Dispose();
        base.Dispose(disposing);
    }

    private const byte AC_SRC_OVER = 0;
    private const byte AC_SRC_ALPHA = 1;
    private const int ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Width, Height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
