using System.Drawing;

namespace Windshot.Capture;

/// <summary>
/// Makes the corners of a window capture transparent where Windows 11 rounds them, so the
/// desktop or windows behind don't show in the corners. Edges are anti-aliased like DWM's.
/// </summary>
internal static class CornerMask
{
    /// <summary>Sub-samples per pixel side when working out how much of a pixel is inside the curve.</summary>
    private const int Samples = 4;

    /// <param name="window">The window's bounds in desktop pixels; corners outside the image are skipped.</param>
    /// <param name="radius">Corner radius in physical pixels.</param>
    public static void Apply(CapturedImage image, Rectangle window, int radius)
    {
        if (radius <= 0)
            return;
        var image0 = image.DesktopBounds.Location;
        Corner(window.Left, window.Top, 1, 1);
        Corner(window.Right, window.Top, -1, 1);
        Corner(window.Left, window.Bottom, 1, -1);
        Corner(window.Right, window.Bottom, -1, -1);

        // (x, y) is the window's outer corner; (dx, dy) points into the window.
        void Corner(int x, int y, int dx, int dy)
        {
            // The curve's center, as a point between pixels (desktop coordinates).
            double cx = x + dx * radius, cy = y + dy * radius;
            for (int j = 0; j < radius; j++)
            {
                for (int i = 0; i < radius; i++)
                {
                    // The pixel i, j steps in from the corner.
                    int px = dx > 0 ? x + i : x - 1 - i;
                    int py = dy > 0 ? y + j : y - 1 - j;
                    int ix = px - image0.X, iy = py - image0.Y;
                    if (ix < 0 || iy < 0 || ix >= image.Width || iy >= image.Height)
                        continue;

                    int inside = 0;
                    for (int sy = 0; sy < Samples; sy++)
                    {
                        for (int sx = 0; sx < Samples; sx++)
                        {
                            double ox = px + (sx + 0.5) / Samples - cx, oy = py + (sy + 0.5) / Samples - cy;
                            if (ox * ox + oy * oy <= radius * radius)
                                inside++;
                        }
                    }
                    if (inside == Samples * Samples)
                        continue;

                    // Pixels are premultiplied, so color fades along with alpha.
                    int at = (iy * image.Width + ix) * 4;
                    for (int c = 0; c < 4; c++)
                        image.Pixels[at + c] = (byte)(image.Pixels[at + c] * inside / (Samples * Samples));
                }
            }
        }
    }
}
