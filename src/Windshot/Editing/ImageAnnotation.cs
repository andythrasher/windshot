using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;

namespace Windshot.Editing;

/// <summary>
/// An inserted picture (a PNG, JPEG or WebP file, or one pasted or dropped in), stretched over
/// its box. Its corners keep its proportions unless Shift is held. The bitmap never changes,
/// so undo copies share it.
/// </summary>
internal sealed class ImageAnnotation : BoxAnnotation
{
    public ImageAnnotation(CanvasBitmap bitmap, Rect box, float unit)
        : base(new Vector2((float)box.Left, (float)box.Top), Microsoft.UI.Colors.Transparent, Annotation.MinWeight, unit)
    {
        Bitmap = bitmap;
        End = new Vector2((float)box.Right, (float)box.Bottom);
    }

    public CanvasBitmap Bitmap { get; }

    /// <summary>The file it came from, without the extension, for its name in the layers panel.</summary>
    public string? Title { get; init; }

    public override bool HasColor => false;

    public override bool HasCorners => true;

    public override Rect Frame => Shape;

    protected override void DrawUnrotated(CanvasDrawingSession ds)
    {
        var box = Shape;
        if (box.Width < 1 || box.Height < 1)
            return;
        var source = new Rect(0, 0, Bitmap.SizeInPixels.Width, Bitmap.SizeInPixels.Height);
        float radius = Math.Min(Style.CornerRadius(Unit), (float)Math.Min(box.Width, box.Height) / 2);
        if (radius <= 0)
        {
            ds.DrawImage(Bitmap, box, source, 1, CanvasImageInterpolation.HighQualityCubic);
            return;
        }
        using var corners = CanvasGeometry.CreateRoundedRectangle(ds, box, radius, radius);
        using (ds.CreateLayer(1, corners))
            ds.DrawImage(Bitmap, box, source, 1, CanvasImageInterpolation.HighQualityCubic);
    }

    protected override bool HitTestUnrotated(Vector2 point, float tolerance) =>
        Shape.Inflate(tolerance).Contains(point.ToPoint());
}
