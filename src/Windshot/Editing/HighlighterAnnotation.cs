using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// A straight, marker-style stroke. It blends with "darken" (per-channel minimum) into
/// whatever is beneath it in the layer stack, so text underneath stays fully black instead of
/// being tinted, the way real highlighter ink behaves. It never grows the canvas.
/// </summary>
internal sealed class HighlighterAnnotation : TwoPointAnnotation
{
    private const float SnapDegrees = 5;
    private static readonly float SnapSlope = MathF.Tan(SnapDegrees * MathF.PI / 180);

    public HighlighterAnnotation(Vector2 start, Color color, int weight, float unit)
        : base(start, color, weight, unit)
    {
    }

    public override bool ExtendsCanvas => false;

    public override bool IsEffect => true;

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
            return new Rect(minX, minY, points.Max(p => p.X) - minX, points.Max(p => p.Y) - minY);
        }
    }

    /// <summary>Drawn by <see cref="ApplyTo"/>, as part of the layer stack.</summary>
    public override void Draw(CanvasDrawingSession ds)
    {
    }

    public override ICanvasImage ApplyTo(LayerContext context, ICanvasImage below)
    {
        var result = context.NewList();
        using var s = result.CreateDrawingSession();
        s.DrawImage(below);
        // Over transparent canvas the minimum is transparent, so ink only lands on what's there.
        using var stroke = CanvasGeometry.CreatePolygon(s, Outline());
        s.Blend = CanvasBlend.Min;
        s.FillGeometry(stroke, Ink);
        return result;
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
