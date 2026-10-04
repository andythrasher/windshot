using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// A straight, marker-style stroke. It blends with "darken" (per-channel minimum), so the
/// text underneath stays fully black instead of being tinted, the way real highlighter ink
/// behaves. It marks the screenshot only: it's clipped to the image and never grows the canvas.
/// </summary>
internal sealed class HighlighterAnnotation : TwoPointAnnotation
{
    private const float SnapDegrees = 5;
    private static readonly float SnapSlope = MathF.Tan(SnapDegrees * MathF.PI / 180);

    private readonly Rect _imageBounds;

    public HighlighterAnnotation(Vector2 start, Rect imageBounds, Color color, int weight, float unit)
        : base(start, color, weight, unit)
    {
        _imageBounds = imageBounds;
    }

    public override bool ExtendsCanvas => false;

    public float Thickness => (8 + Weight * 3) * Unit;

    /// <summary>The palette color softened toward white, like highlighter ink.</summary>
    private Color Ink => Color.FromArgb(255, Soften(Color.R), Soften(Color.G), Soften(Color.B));

    private static byte Soften(byte channel) => (byte)(channel + (255 - channel) * 0.4);

    public override void MoveHandle(int index, Vector2 position)
    {
        // Highlights almost always run along a line of text, so snap near-horizontal strokes flat.
        var anchor = index == 0 ? End : Start;
        var delta = position - anchor;
        if (delta.X != 0 && Math.Abs(delta.Y) <= Math.Abs(delta.X) * SnapSlope)
            position.Y = anchor.Y;
        base.MoveHandle(index, position);
    }

    private Vector2[] Outline()
    {
        var delta = End - Start;
        float length = delta.Length();
        var dir = length < 0.5f ? Vector2.UnitX : delta / length;
        var half = new Vector2(-dir.Y, dir.X) * (Thickness / 2);
        return [Start + half, End + half, End - half, Start - half];
    }

    public override Rect Bounds
    {
        get
        {
            var points = Outline();
            float minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
            var bounds = new Rect(minX, minY, points.Max(p => p.X) - minX, points.Max(p => p.Y) - minY);
            bounds.Intersect(_imageBounds);
            return bounds;
        }
    }

    public override void Draw(CanvasDrawingSession ds)
    {
        using var stroke = CanvasGeometry.CreatePolygon(ds, Outline());
        using var image = CanvasGeometry.CreateRectangle(ds, _imageBounds);
        using var clipped = stroke.CombineWith(image, Matrix3x2.Identity, CanvasGeometryCombine.Intersect);

        var blend = ds.Blend;
        ds.Blend = CanvasBlend.Min;
        ds.FillGeometry(clipped, Ink);
        ds.Blend = blend;
    }

    public override bool HitTest(Vector2 point, float tolerance)
    {
        // Distance from the point to the stroke's center line.
        var segment = End - Start;
        float lengthSquared = segment.LengthSquared();
        float t = lengthSquared == 0 ? 0 : Math.Clamp(Vector2.Dot(point - Start, segment) / lengthSquared, 0, 1);
        return Vector2.Distance(point, Start + segment * t) <= Thickness / 2 + tolerance;
    }
}
