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
    private ScreenSnapshot(Bitmap bitmap, Rectangle virtualBounds)
    {
        Bitmap = bitmap;
        VirtualBounds = virtualBounds;
    }

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
        return new ScreenSnapshot(bitmap, bounds);
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
        return new CapturedImage(pixels, local.Width, local.Height, scale, desktop);
    }

    public void Dispose() => Bitmap.Dispose();
}
