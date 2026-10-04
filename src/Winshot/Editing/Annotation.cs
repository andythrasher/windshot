using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace Winshot.Editing;

/// <summary>
/// A vector object drawn over the screenshot. Annotations stay editable until export;
/// they are never baked into the bitmap. Coordinates are in image pixels.
/// </summary>
internal abstract class Annotation
{
    public abstract Rect Bounds { get; }

    public abstract void Draw(CanvasDrawingSession ds);

    public abstract bool HitTest(Vector2 point, float tolerance);

    public abstract void MoveBy(Vector2 delta);
}

internal sealed class RectangleAnnotation : Annotation
{
    public RectangleAnnotation(Vector2 start, Color color, float thickness)
    {
        Start = End = start;
        Color = color;
        Thickness = thickness;
    }

    public Vector2 Start { get; set; }
    public Vector2 End { get; set; }
    public Color Color { get; set; }
    public float Thickness { get; set; }

    public Rect Shape => new(Start.ToPoint(), End.ToPoint());

    public override Rect Bounds => Shape.Inflate(Thickness / 2);

    public override void Draw(CanvasDrawingSession ds) =>
        ds.DrawRoundedRectangle(Shape, Thickness / 2, Thickness / 2, Color, Thickness);

    public override bool HitTest(Vector2 point, float tolerance)
    {
        // Only the stroke is grabbable, so you can still click things inside the rectangle.
        float reach = Thickness / 2 + tolerance;
        var outer = Shape.Inflate(reach);
        var inner = Shape.Inflate(-reach);
        var p = point.ToPoint();
        return outer.Contains(p) && (inner.IsEmpty || !inner.Contains(p));
    }

    public override void MoveBy(Vector2 delta)
    {
        Start += delta;
        End += delta;
    }
}
