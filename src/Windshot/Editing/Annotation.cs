using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// A vector object drawn over the screenshot. Annotations stay editable until export;
/// they are never baked into the bitmap. Coordinates are in image pixels.
/// </summary>
/// <remarks>
/// Each type describes itself in its own frame, unrotated; <see cref="Angle"/> turns that
/// frame on the canvas. The public members (bounds, drawing, hit-testing, handles) work in
/// canvas coordinates and do the turning; the protected ones they wrap work in the frame.
/// Types made of free points (arrows, highlights, polygons) turn their points instead and
/// keep an angle of zero.
/// </remarks>
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

    /// <summary>Whether the layer takes a color from the palette (blurs, spotlights and images don't).</summary>
    public virtual bool HasColor => !IsAreaEffect;

    // ---- Style -------------------------------------------------------------------------

    /// <summary>Outline, shadow and corner rounding; see <see cref="LayerStyle"/>.</summary>
    public LayerStyle Style { get; set; } = LayerStyle.None;

    /// <summary>Whether outline and shadow apply: to layers that draw something, not to effects.</summary>
    public bool CanStyle => !IsEffect;

    /// <summary>Whether the layer has corners for <see cref="LayerStyle.Corners"/> to round.</summary>
    public virtual bool HasCorners => false;

    /// <summary>How far the outline and shadow reach past the layer.</summary>
    private float StyleMargin => CanStyle ? Style.Margin(Unit) : 0;

    // ---- Rotation ----------------------------------------------------------------------

    /// <summary>How far the frame is turned, in radians, clockwise on screen; between -π and π.</summary>
    public float Angle { get; private set; }

    /// <summary>The point the frame turns about, in canvas coordinates (it stays put when turned).</summary>
    private Vector2 _pivot;

    /// <summary>From the frame to the canvas.</summary>
    public Matrix3x2 Rotation => Angle == 0 ? Matrix3x2.Identity : Matrix3x2.CreateRotation(Angle, _pivot);

    public Vector2 ToCanvas(Vector2 framePoint) => Angle == 0 ? framePoint : Vector2.Transform(framePoint, Rotation);

    public Vector2 ToFrame(Vector2 canvasPoint) =>
        Angle == 0 ? canvasPoint : Vector2.Transform(canvasPoint, Matrix3x2.CreateRotation(-Angle, _pivot));

    /// <summary>Turns the layer by <paramref name="radians"/> about <paramref name="around"/> (canvas coordinates).</summary>
    public virtual void RotateBy(float radians, Vector2 around)
    {
        float angle = Angle + radians;
        angle = MathF.IEEERemainder(angle, MathF.Tau);
        // Turned back to straight (give or take rounding): exactly straight, so it takes the plain path.
        Angle = MathF.Abs(angle) < 1e-4f ? 0 : angle;
        // Turning about another point is turning about the pivot, then moving over by how far
        // the pivot itself swings around that point.
        var pivot = _pivot;
        MoveBy(Vector2.Transform(pivot, Matrix3x2.CreateRotation(radians, around)) - pivot);
    }

    // ---- Geometry, in canvas coordinates -----------------------------------------------

    /// <summary>The bounds in the layer's own frame, before turning.</summary>
    public abstract Rect Frame { get; }

    /// <summary>The axis-aligned bounds on the canvas, of the turned frame, plus any outline and shadow.</summary>
    public Rect Bounds
    {
        get
        {
            var frame = Frame;
            if (Angle != 0)
            {
                var corners = Corners(frame);
                float minX = corners.Min(p => p.X), minY = corners.Min(p => p.Y);
                frame = new Rect(minX, minY, corners.Max(p => p.X) - minX, corners.Max(p => p.Y) - minY);
            }
            float margin = StyleMargin;
            return margin > 0 ? frame.Inflate(margin) : frame;
        }
    }

    /// <summary>The corners of a rectangle in the frame, on the canvas: clockwise from top-left.</summary>
    public Vector2[] Corners(Rect frame) =>
    [
        ToCanvas(new((float)frame.Left, (float)frame.Top)), ToCanvas(new((float)frame.Right, (float)frame.Top)),
        ToCanvas(new((float)frame.Right, (float)frame.Bottom)), ToCanvas(new((float)frame.Left, (float)frame.Bottom)),
    ];

    public void Draw(CanvasDrawingSession ds) => DrawRotated(ds, DrawUnrotated);

    /// <summary>Draws in the frame, turned onto the canvas.</summary>
    protected void DrawRotated(CanvasDrawingSession ds, Action<CanvasDrawingSession> draw)
    {
        if (Angle == 0)
        {
            draw(ds);
            return;
        }
        var transform = ds.Transform;
        ds.Transform = Rotation * transform;
        try
        {
            draw(ds);
        }
        finally
        {
            ds.Transform = transform;
        }
    }

    public bool HitTest(Vector2 point, float tolerance) => HitTestUnrotated(ToFrame(point), tolerance);

    /// <summary>Moves the layer across the canvas.</summary>
    public void MoveBy(Vector2 delta)
    {
        Offset(delta);
        _pivot += delta;
    }

    /// <summary>Draggable control points (endpoints, corners). Empty if the object can only be moved.</summary>
    public IReadOnlyList<Vector2> Handles
    {
        get
        {
            var handles = UnrotatedHandles;
            return Angle == 0 ? handles : handles.Select(ToCanvas).ToList();
        }
    }

    public void MoveHandle(int index, Vector2 position) => MoveUnrotatedHandle(index, ToFrame(position));

    // ---- Geometry, in the frame --------------------------------------------------------

    protected abstract void DrawUnrotated(CanvasDrawingSession ds);

    protected abstract bool HitTestUnrotated(Vector2 point, float tolerance);

    /// <summary>Moves the layer's points within its frame.</summary>
    protected abstract void Offset(Vector2 delta);

    protected virtual IReadOnlyList<Vector2> UnrotatedHandles => [];

    protected virtual void MoveUnrotatedHandle(int index, Vector2 position)
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

    protected override IReadOnlyList<Vector2> UnrotatedHandles => [Start, End];

    protected override void MoveUnrotatedHandle(int index, Vector2 position)
    {
        if (index == 0)
            Start = position;
        else
            End = position;
    }

    protected override void Offset(Vector2 delta)
    {
        Start += delta;
        End += delta;
    }

    /// <summary>For lines (arrows, highlights): turns the two points themselves, so the angle stays zero.</summary>
    protected void RotatePoints(float radians, Vector2 around)
    {
        var turn = Matrix3x2.CreateRotation(radians, around);
        Start = Vector2.Transform(Start, turn);
        End = Vector2.Transform(End, turn);
    }
}

/// <summary>A two-point annotation that is a box (in its frame), resizable from any corner.</summary>
internal abstract class BoxAnnotation : TwoPointAnnotation
{
    protected BoxAnnotation(Vector2 start, Color color, int weight, float unit)
        : base(start, color, weight, unit)
    {
    }

    /// <summary>The box, in the frame.</summary>
    public Rect Shape => new(Start.ToPoint(), End.ToPoint());

    /// <summary>All four corners: Start, End, then the two mixed corners. Each one's opposite is its index ^ 1.</summary>
    protected override IReadOnlyList<Vector2> UnrotatedHandles =>
        [Start, End, new Vector2(Start.X, End.Y), new Vector2(End.X, Start.Y)];

    protected override void MoveUnrotatedHandle(int index, Vector2 position)
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
