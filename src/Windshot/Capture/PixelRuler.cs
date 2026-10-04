using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Windshot.Capture;

/// <summary>
/// The run of same-colored pixels through a point, horizontally and vertically, in
/// inclusive pixel coordinates: e.g. a button's width and height when you hover inside it.
/// </summary>
internal readonly record struct Measurement(Point Origin, int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left + 1;
    public int Height => Bottom - Top + 1;
}

/// <summary>
/// Shottr-style smart ruler: from a point, scan outward until the color changes. Holds a
/// copy of the monitor's pixels for fast lookups, made the first time the ruler is used.
/// </summary>
internal sealed class PixelRuler
{
    /// <summary>
    /// How far a channel may drift and still count as "the same color", so faint gradients
    /// and compression noise don't stop the scan.
    /// </summary>
    private const int Tolerance = 12;

    private readonly int[] _pixels;
    private readonly int _width;
    private readonly int _height;

    public PixelRuler(Bitmap bitmap)
    {
        _width = bitmap.Width;
        _height = bitmap.Height;
        _pixels = new int[_width * _height];
        var data = bitmap.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < _height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, _pixels, y * _width, _width);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public Measurement Measure(Point p)
    {
        int x = Math.Clamp(p.X, 0, _width - 1), y = Math.Clamp(p.Y, 0, _height - 1);
        int origin = _pixels[y * _width + x];

        int left = x, right = x, top = y, bottom = y;
        while (left > 0 && Same(_pixels[y * _width + left - 1], origin)) left--;
        while (right < _width - 1 && Same(_pixels[y * _width + right + 1], origin)) right++;
        while (top > 0 && Same(_pixels[(top - 1) * _width + x], origin)) top--;
        while (bottom < _height - 1 && Same(_pixels[(bottom + 1) * _width + x], origin)) bottom++;
        return new Measurement(new Point(x, y), left, top, right, bottom);
    }

    private static bool Same(int a, int b) =>
        Math.Abs((a >> 16 & 0xFF) - (b >> 16 & 0xFF)) <= Tolerance &&
        Math.Abs((a >> 8 & 0xFF) - (b >> 8 & 0xFF)) <= Tolerance &&
        Math.Abs((a & 0xFF) - (b & 0xFF)) <= Tolerance;
}
