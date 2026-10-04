using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Windshot.Capture;

/// <summary>
/// The magnifier that follows the cursor on the capture overlay: the pixels around the
/// cursor at 8x, with the color under it and the keyboard shortcuts below.
/// </summary>
internal static class Loupe
{
    private const int Cells = 17;            // odd, so one pixel sits exactly in the middle
    private const float CellDip = 8;
    private const float GapDip = 20;         // distance from the cursor
    private const float CaptionDip = 62;     // three lines: color, position, keys

    private static int Cell(double scale) => Math.Max(4, (int)Math.Round(CellDip * scale));

    /// <summary>Where the loupe goes for a cursor position: below-right, flipped near the edges.</summary>
    public static Rectangle Bounds(Point cursor, Size client, double scale)
    {
        int size = Cells * Cell(scale);
        int width = size, height = size + (int)(CaptionDip * scale);
        int gap = (int)(GapDip * scale);
        int x = cursor.X + gap, y = cursor.Y + gap;
        if (x + width > client.Width)
            x = cursor.X - gap - width;
        if (y + height > client.Height)
            y = cursor.Y - gap - height;
        return new Rectangle(x, y, width, height);
    }

    /// <param name="source">The monitor's frozen pixels, in the same coordinates as <paramref name="cursor"/>.</param>
    /// <param name="position">The cursor position to show, in desktop pixels.</param>
    public static void Draw(Graphics g, Bitmap source, Point cursor, Rectangle bounds, double scale, Color color, Point position)
    {
        int cell = Cell(scale);
        var zoom = new Rectangle(bounds.X, bounds.Y, Cells * cell, Cells * cell);

        var state = g.Save();
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        // Magnified pixels; anything past the monitor's edge stays black.
        g.FillRectangle(Brushes.Black, zoom);
        var sample = new Rectangle(cursor.X - Cells / 2, cursor.Y - Cells / 2, Cells, Cells);
        var visible = Rectangle.Intersect(sample, new Rectangle(Point.Empty, source.Size));
        if (!visible.IsEmpty)
        {
            var dest = new Rectangle(zoom.X + (visible.X - sample.X) * cell, zoom.Y + (visible.Y - sample.Y) * cell,
                visible.Width * cell, visible.Height * cell);
            g.DrawImage(source, dest, visible, GraphicsUnit.Pixel);
        }
        g.PixelOffsetMode = PixelOffsetMode.None;

        // Mid-grey so the grid shows on light and dark pixels alike.
        using (var grid = new Pen(Color.FromArgb(48, 128, 128, 128)))
        {
            for (int i = 1; i < Cells; i++)
            {
                g.DrawLine(grid, zoom.X + i * cell, zoom.Y, zoom.X + i * cell, zoom.Bottom - 1);
                g.DrawLine(grid, zoom.X, zoom.Y + i * cell, zoom.Right - 1, zoom.Y + i * cell);
            }
        }

        // The pixel under the cursor, ringed so it shows on light and dark alike.
        var center = new Rectangle(zoom.X + Cells / 2 * cell, zoom.Y + Cells / 2 * cell, cell, cell);
        g.DrawRectangle(Pens.Black, center.X - 1, center.Y - 1, center.Width + 1, center.Height + 1);
        g.DrawRectangle(Pens.White, center.X, center.Y, center.Width - 1, center.Height - 1);

        // Caption: swatch, hex, position and the keys.
        var caption = new Rectangle(zoom.X, zoom.Bottom, zoom.Width, bounds.Bottom - zoom.Bottom);
        using (var bg = new SolidBrush(Color.FromArgb(240, 32, 32, 32)))
            g.FillRectangle(bg, caption);
        using (var frame = new Pen(Color.FromArgb(160, 0, 0, 0)))
            g.DrawRectangle(frame, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);

        int pad = (int)(8 * scale);
        int swatchSize = (int)(16 * scale);
        var swatch = new Rectangle(caption.X + pad, caption.Y + pad, swatchSize, swatchSize);
        using (var fill = new SolidBrush(color))
            g.FillRectangle(fill, swatch);
        g.DrawRectangle(Pens.White, swatch.X, swatch.Y, swatch.Width - 1, swatch.Height - 1);

        using var hexFont = new Font("Cascadia Mono", (float)(13 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", (float)(11 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
        var dim = Color.FromArgb(170, 170, 170);
        int line = (int)(16 * scale);
        string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        TextRenderer.DrawText(g, hex, hexFont,
            new Rectangle(swatch.Right + pad, swatch.Y, caption.Right - swatch.Right - pad, swatchSize), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, $"{position.X}, {position.Y}", smallFont,
            new Point(caption.X + pad, swatch.Bottom + (int)(4 * scale)), dim);
        TextRenderer.DrawText(g, "C color · R ruler · M hide", smallFont,
            new Point(caption.X + pad, swatch.Bottom + (int)(4 * scale) + line), dim);

        g.Restore(state);
    }
}
