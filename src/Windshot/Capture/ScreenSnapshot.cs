using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Windshot.Capture;

/// <summary>
/// Premultiplied BGRA pixels handed from the capture side to the editor. Screen captures are
/// opaque, apart from the rounded corners of a captured window.
/// </summary>
/// <param name="Scale">Device pixels per DIP on the monitor the region came from.</param>
/// <param name="DesktopBounds">Where the region was on screen, in physical pixels.</param>
/// <param name="IsScrolling">Stitched from a scrolling capture, so likely much taller than the screen.</param>
internal sealed record CapturedImage(byte[] Pixels, int Width, int Height, double Scale, Rectangle DesktopBounds, bool IsScrolling = false);

/// <summary>A frozen copy of the entire virtual desktop, in physical pixels.</summary>
internal sealed class ScreenSnapshot : IDisposable
{
    private ScreenSnapshot(Bitmap bitmap, Rectangle virtualBounds, Rectangle[] monitors)
    {
        Bitmap = bitmap;
        VirtualBounds = virtualBounds;
        _monitors = monitors;
    }

    /// <summary>
    /// Where the monitors are. The virtual desktop is their bounding box, so with monitors of
    /// different sizes some of it is on no monitor at all.
    /// </summary>
    private readonly Rectangle[] _monitors;

    public Bitmap Bitmap { get; }

    /// <summary>Desktop-space rectangle the bitmap covers. Its origin can be negative.</summary>
    public Rectangle VirtualBounds { get; }

    public static ScreenSnapshot Take()
    {
        var bounds = SystemInformation.VirtualScreen;
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            // With DWM composition a plain BitBlt already includes layered windows.
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        }
        return new ScreenSnapshot(bitmap, bounds, Monitors.All().Select(m => m.Bounds).ToArray());
    }

    /// <summary>Converts a desktop-space rectangle to bitmap coordinates.</summary>
    public Rectangle ToLocal(Rectangle desktopRect)
    {
        var local = desktopRect;
        local.Offset(-VirtualBounds.X, -VirtualBounds.Y);
        return local;
    }

    public CapturedImage Crop(Rectangle desktopRect, double scale)
    {
        var local = Rectangle.Intersect(ToLocal(desktopRect), new Rectangle(Point.Empty, Bitmap.Size));
        int rowBytes = local.Width * 4;
        var pixels = new byte[rowBytes * local.Height];

        // Locking as 32bppArgb converts from the RGB source and fills alpha with 255.
        var data = Bitmap.LockBits(local, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < local.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowBytes, rowBytes);
        }
        finally
        {
            Bitmap.UnlockBits(data);
        }

        var desktop = local;
        desktop.Offset(VirtualBounds.Location);
        ClearOffMonitor(pixels, desktop);
        return new CapturedImage(pixels, local.Width, local.Height, scale, desktop);
    }

    /// <summary>
    /// Makes the parts of an area that are on no monitor transparent, e.g. below a shorter
    /// monitor in a selection that spans two. They'd otherwise be solid black.
    /// </summary>
    private void ClearOffMonitor(byte[] pixels, Rectangle area)
    {
        long onMonitors = _monitors.Sum(m =>
        {
            var part = Rectangle.Intersect(m, area);
            return (long)part.Width * part.Height;
        });
        if (onMonitors >= (long)area.Width * area.Height)
            return;

        int rowBytes = area.Width * 4;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            int row = (y - area.Top) * rowBytes;
            int x = area.Left;
            foreach (var m in _monitors.Where(m => y >= m.Top && y < m.Bottom).OrderBy(m => m.Left))
            {
                int start = Math.Max(m.Left, area.Left), end = Math.Min(m.Right, area.Right);
                if (start > x)
                    Array.Clear(pixels, row + (x - area.Left) * 4, (Math.Min(start, area.Right) - x) * 4);
                x = Math.Max(x, end);
            }
            if (x < area.Right)
                Array.Clear(pixels, row + (x - area.Left) * 4, (area.Right - x) * 4);
        }
    }

    public void Dispose() => Bitmap.Dispose();
}
