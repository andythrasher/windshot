using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// A vector object drawn over the screenshot. Annotations stay editable until export;
/// they are never baked into the bitmap. Coordinates are in image pixels.
/// </summary>
internal abstract class Annotation
{
    public const int MinWeight = 1;
    public const int MaxWeight = 10;

    protected Annotation(Color color, int weight, float unit)
    {
        Color = color;
        Weight = weight;
        Unit = unit;
    }

    private static long _created;

    public Color Color { get; set; }

    /// <summary>Size level from <see cref="MinWeight"/> to <see cref="MaxWeight"/>; each type maps it to its own geometry.</summary>
    public int Weight { get; set; }

    // ---- As a layer --------------------------------------------------------------------

    /// <summary>Hidden layers aren't drawn, clicked, exported or counted toward the canvas size.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>0 to 1: how strongly the layer shows over (or, for effects, changes) what's beneath it.</summary>
    public float Opacity { get; set; } = 1;

    /// <summary>A name given in the layers panel; null shows the default one.</summary>
    public string? Name { get; set; }

    /// <summary>Order of creation (copies keep it), which numbers steps independently of stacking.</summary>
    public long Created { get; } = Interlocked.Increment(ref _created);

    /// <summary>
    /// Effects (blur, spotlight, highlight) change what's beneath them, like adjustment layers,
    /// instead of drawing on top; see <see cref="ApplyTo"/>.
    /// </summary>
    public virtual bool IsEffect => false;

    /// <summary>For effects: everything beneath, with this effect applied.</summary>
    public virtual ICanvasImage ApplyTo(LayerContext context, ICanvasImage below) => below;

    /// <summary>Image pixels per DIP on the source monitor, so a weight looks the same on any display.</summary>
    public float Unit { get; }

    /// <summary>
    /// Area effects (blur, spotlight) change a region of the screenshot itself: they paint
    /// beneath other annotations, never extend the canvas, and are only grabbable with the
    /// Select tool or their own tool, so you can still start an arrow on top of them.
    /// </summary>
    public virtual bool IsAreaEffect => false;

    /// <summary>Whether sticking out past the image grows the canvas (the "reverse crop").</summary>
    public virtual bool ExtendsCanvas => !IsAreaEffect;

    public abstract Rect Bounds { get; }

    public abstract void Draw(CanvasDrawingSession ds);

    public abstract bool HitTest(Vector2 point, float tolerance);

    public abstract void MoveBy(Vector2 delta);

    /// <summary>Draggable control points (endpoints, corners). Empty if the object can only be moved.</summary>
    public virtual IReadOnlyList<Vector2> Handles => [];

    public virtual void MoveHandle(int index, Vector2 position)
    {
    }

    /// <summary>An independent copy, used for undo snapshots.</summary>
    public Annotation Clone()
    {
        var copy = (Annotation)MemberwiseClone();
        copy.OnCloned();
        return copy;
    }

    /// <summary>Drop anything the copy must not share with the original, such as cached GPU resources.</summary>
    protected virtual void OnCloned()
    {
    }
}

/// <summary>An annotation defined by two points, created by dragging from Start to End.</summary>
internal abstract class TwoPointAnnotation : Annotation
{
    /// <summary>Handle index of <see cref="End"/>, which is what a creation drag moves.</summary>
    public const int EndHandle = 1;

    protected TwoPointAnnotation(Vector2 start, Color color, int weight, float unit)
        : base(color, weight, unit)
    {
        Start = End = start;
    }

    public Vector2 Start { get; set; }
    public Vector2 End { get; set; }

    public override IReadOnlyList<Vector2> Handles => [Start, End];

    public override void MoveHandle(int index, Vector2 position)
    {
        if (index == 0)
            Start = position;
        else
            End = position;
    }

    public override void MoveBy(Vector2 delta)
    {
        Start += delta;
        End += delta;
    }
}

internal sealed class RectangleAnnotation : BoxAnnotation
{
    public RectangleAnnotation(Vector2 start, Color color, int weight, float unit)
        : base(start, color, weight, unit)
    {
    }

    public float Thickness => (1 + Weight) * Unit;

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
}

/// <summary>A two-point annotation that is an axis-aligned box, resizable from any corner.</summary>
internal abstract class BoxAnnotation : TwoPointAnnotation
{
    protected BoxAnnotation(Vector2 start, Color color, int weight, float unit)
        : base(start, color, weight, unit)
    {
    }

    public Rect Shape => new(Start.ToPoint(), End.ToPoint());

    /// <summary>All four corners: Start, End, then the two mixed corners.</summary>
    public override IReadOnlyList<Vector2> Handles =>
        [Start, End, new Vector2(Start.X, End.Y), new Vector2(End.X, Start.Y)];

    public override void MoveHandle(int index, Vector2 position)
    {
        switch (index)
        {
            case 0: Start = position; break;
            case 1: End = position; break;
            case 2: Start = new Vector2(position.X, Start.Y); End = new Vector2(End.X, position.Y); break;
            case 3: End = new Vector2(position.X, End.Y); Start = new Vector2(Start.X, position.Y); break;
        }
    }
}
