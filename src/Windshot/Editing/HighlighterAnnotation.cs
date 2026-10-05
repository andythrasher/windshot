using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>How a highlight mixes with what's under it, like a layer's blend mode in a photo editor.</summary>
internal enum HighlightBlend
{
    /// <summary>Like real highlighter ink: everything under it darkens by the ink's color, text included.</summary>
    Multiply,
    /// <summary>Keeps the darker of the ink and what's under it: black text stays black, lighter text turns ink-colored.</summary>
    Darken,
    /// <summary>Tints and adds contrast: darks get darker and lights lighter.</summary>
    Overlay,
    /// <summary>For dark backgrounds: lightens what's under it by the ink's color.</summary>
    Screen,
}

/// <summary>
/// A straight, marker-style stroke that blends into whatever is beneath it in the layer
/// stack (<see cref="Blend"/>). It never grows the canvas.
/// </summary>
internal sealed class HighlighterAnnotation : TwoPointAnnotation
{
    public HighlightBlend Blend { get; set; } = HighlightBlend.Multiply;

    private const float SnapDegrees = 5;
    private static readonly float SnapSlope = MathF.Tan(SnapDegrees * MathF.PI / 180);

    public HighlighterAnnotation(Vector2 start, Color color, int weight, float unit)
        : base(start, color, weight, unit)
    {
    }

    public override bool ExtendsCanvas => false;

    public override bool IsEffect => true;

    public float Thickness => (8 + Weight * 3) * Unit;

    /// <summary>
    /// The palette color softened toward white, like highlighter ink, for the darkening modes;
    /// at full strength, the softer ink would make Overlay and Screen too faint.
    /// </summary>
    private Color Ink => Blend is HighlightBlend.Multiply or HighlightBlend.Darken
        ? Color.FromArgb(255, Soften(Color.R), Soften(Color.G), Soften(Color.B))
        : Color;

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
        var ink = context.NewList();
        using (var s = ink.CreateDrawingSession())
        using (var stroke = CanvasGeometry.CreatePolygon(s, Outline()))
            s.FillGeometry(stroke, Ink);
        var blended = context.Own(new BlendEffect { Background = below, Foreground = ink, Mode = Mode(Blend) });
        // Kept to what's there, so ink never lands on the transparent canvas around the image.
        return context.Own(new CompositeEffect { Mode = CanvasComposite.SourceAtop, Sources = { below, blended } });
    }

    private static BlendEffectMode Mode(HighlightBlend blend) => blend switch
    {
        HighlightBlend.Darken => BlendEffectMode.Darken,
        HighlightBlend.Overlay => BlendEffectMode.Overlay,
        HighlightBlend.Screen => BlendEffectMode.Screen,
        _ => BlendEffectMode.Multiply,
    };

    public override bool HitTest(Vector2 point, float tolerance)
    {
        // Distance from the point to the stroke's center line.
        var segment = End - Start;
        float lengthSquared = segment.LengthSquared();
        float t = lengthSquared == 0 ? 0 : Math.Clamp(Vector2.Dot(point - Start, segment) / lengthSquared, 0, 1);
        return Vector2.Distance(point, Start + segment * t) <= Thickness / 2 + tolerance;
    }
}
