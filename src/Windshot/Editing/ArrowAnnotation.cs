using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// A filled arrow: a shaft that tapers toward the tail, and a head with swept-back barbs.
/// Every dimension is proportional to one thickness, so a heavier weight makes the whole
/// arrow stouter instead of just fattening an outline.
/// </summary>
internal sealed class ArrowAnnotation : TwoPointAnnotation
{
    // Proportions relative to the shaft thickness just behind the head.
    private const float HeadLengthRatio = 3.0f;
    private const float HeadHalfWidthRatio = 1.6f;
    private const float NeckHalfWidthRatio = 0.5f;
    private const float TailHalfWidthRatio = 0.15f;
    private const float BarbSweep = 0.25f; // how far the barbs reach back past the neck, as a fraction of head length
    private const float MaxHeadFraction = 0.6f; // the head never takes more than this much of a short arrow

    private static readonly CanvasStrokeStyle RoundJoin = new() { LineJoin = CanvasLineJoin.Round };

    public ArrowAnnotation(Vector2 start, Color color, int weight, float unit)
        : base(start, color, weight, unit)
    {
    }

    public float Thickness => (2 + Weight * 2) * Unit;

    private readonly record struct Shape(Vector2 Origin, Vector2 Dir, Vector2 Normal, float Length,
        float Head, float HeadHalf, float Neck, float Tail)
    {
        public Vector2 At(float along, float across) => Origin + Dir * along + Normal * across;
    }

    private Shape? Measure()
    {
        var delta = End - Start;
        float length = delta.Length();
        if (length < 0.5f)
            return null;

        // Short arrows scale down uniformly so the head never overshoots the tail.
        float t = Thickness * Math.Min(1, length * MaxHeadFraction / (Thickness * HeadLengthRatio));
        var dir = delta / length;
        return new Shape(Start, dir, new Vector2(-dir.Y, dir.X), length,
            t * HeadLengthRatio, t * HeadHalfWidthRatio, t * NeckHalfWidthRatio, t * TailHalfWidthRatio);
    }

    private static Vector2[] Outline(Shape s)
    {
        float neckAt = s.Length - s.Head * (1 - BarbSweep);
        float barbAt = s.Length - s.Head;
        return
        [
            s.At(0, s.Tail),
            s.At(neckAt, s.Neck),
            s.At(barbAt, s.HeadHalf),
            s.At(s.Length, 0),
            s.At(barbAt, -s.HeadHalf),
            s.At(neckAt, -s.Neck),
            s.At(0, -s.Tail),
        ];
    }

    /// <summary>A thin same-color stroke with round joins softens the polygon's corners.</summary>
    private static float SoftenWidth(Shape s) => s.Neck * 0.4f;

    public override void RotateBy(float radians, Vector2 around) => RotatePoints(radians, around);

    public override Rect Frame
    {
        get
        {
            if (Measure() is not Shape s)
                return new Rect(Start.ToPoint(), End.ToPoint());

            var points = Outline(s);
            float minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
            float maxX = points.Max(p => p.X), maxY = points.Max(p => p.Y);
            return new Rect(minX, minY, maxX - minX, maxY - minY).Inflate(SoftenWidth(s) / 2);
        }
    }

    protected override void DrawUnrotated(CanvasDrawingSession ds)
    {
        if (Measure() is not Shape s)
            return;

        using var geometry = CanvasGeometry.CreatePolygon(ds, Outline(s));
        ds.FillGeometry(geometry, Color);
        ds.DrawGeometry(geometry, Color, SoftenWidth(s), RoundJoin);
    }

    protected override bool HitTestUnrotated(Vector2 point, float tolerance)
    {
        if (Measure() is not Shape s)
            return Vector2.Distance(point, Start) <= tolerance;

        // Work in the arrow's own frame: distance along the shaft and across it.
        var local = point - s.Origin;
        float along = Vector2.Dot(local, s.Dir);
        float across = Math.Abs(Vector2.Dot(local, s.Normal));
        if (along < -tolerance || along > s.Length + tolerance)
            return false;

        float barbAt = s.Length - s.Head;
        float halfWidth = along >= barbAt
            ? s.HeadHalf * (s.Length - along) / s.Head
            : s.Tail + (s.Neck - s.Tail) * Math.Clamp(along / barbAt, 0, 1);
        return across <= halfWidth + tolerance;
    }
}
