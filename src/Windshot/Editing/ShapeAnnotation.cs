using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace Windshot.Editing;

internal enum ShapeKind
{
    Rectangle,
    Ellipse,
    Triangle,
    /// <summary>Clicked out point by point; see <see cref="PolygonAnnotation"/>.</summary>
    Polygon,
    /// <summary>An icon from <see cref="Stickers"/>, fitted into the box.</summary>
    Sticker,
}

/// <summary>
/// A shape that can be filled with its color, not just outlined. Filled, it also covers
/// whatever is beneath, which makes it the way to blank something out.
/// </summary>
internal interface IFillable
{
    bool Filled { get; set; }
}

/// <summary>Drawing and hit-testing shared by the shapes: an outline, filled or not.</summary>
internal static class ShapeDrawing
{
    public static readonly CanvasStrokeStyle RoundJoin = new() { LineJoin = CanvasLineJoin.Round };

    public static void Draw(CanvasDrawingSession ds, CanvasGeometry shape, Color color, float thickness, bool filled)
    {
        if (filled)
            ds.FillGeometry(shape, color);
        ds.DrawGeometry(shape, color, thickness, RoundJoin);
    }

    /// <summary>
    /// The outline is grabbable; so is the inside of a filled shape. An outlined shape lets
    /// clicks through its middle, so you can still click things inside it.
    /// </summary>
    public static bool HitTest(CanvasGeometry shape, Vector2 point, float thickness, float tolerance, bool filled) =>
        (filled && shape.FillContainsPoint(point)) ||
        shape.StrokeContainsPoint(point, thickness + tolerance * 2, RoundJoin);
}

/// <summary>A rectangle, ellipse, triangle or sticker, dragged out as a box.</summary>
internal sealed class ShapeAnnotation : BoxAnnotation, IFillable
{
    public ShapeAnnotation(Vector2 start, ShapeKind kind, Color color, int weight, float unit)
        : base(start, color, weight, unit)
    {
        Kind = kind;
    }

    /// <summary>Rectangle, Ellipse, Triangle or Sticker; they share the box, so one can become another.</summary>
    public ShapeKind Kind { get; set; }

    /// <summary>For a sticker: its icon's name in <see cref="Stickers.All"/>.</summary>
    public string StickerIcon { get; set; } = Stickers.All[0].Icon;

    public bool Filled { get; set; }

    public float Thickness => (1 + Weight) * Unit;

    public override Rect Frame => Kind == ShapeKind.Sticker ? Shape : Shape.Inflate(Thickness / 2);

    private CanvasGeometry Geometry(ICanvasResourceCreator creator)
    {
        var box = Shape;
        switch (Kind)
        {
            case ShapeKind.Ellipse:
                return CanvasGeometry.CreateEllipse(creator, (float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2),
                    (float)box.Width / 2, (float)box.Height / 2);
            case ShapeKind.Sticker:
                // As large as fits the box, centered in it, keeping the icon's proportions.
                float scale = (float)Math.Min(box.Width, box.Height) / Stickers.GridSize;
                var offset = new Vector2((float)(box.X + (box.Width - Stickers.GridSize * scale) / 2), (float)(box.Y + (box.Height - Stickers.GridSize * scale) / 2));
                return Stickers.Geometry(creator, StickerIcon, Filled, Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(offset));
            case ShapeKind.Triangle:
                // Pointing up, with its base along the bottom of the box.
                float left = (float)box.Left, right = (float)box.Right, top = (float)box.Top, bottom = (float)box.Bottom;
                return CanvasGeometry.CreatePolygon(creator, [new((left + right) / 2, top), new(right, bottom), new(left, bottom)]);
            default:
                // Rounded as far as the style asks, up to a pill.
                float radius = Math.Min(Thickness / 2 + Style.CornerRadius(Unit), (float)Math.Min(box.Width, box.Height) / 2);
                return CanvasGeometry.CreateRoundedRectangle(creator, box, radius, radius);
        }
    }

    public override bool HasCorners => Kind == ShapeKind.Rectangle;

    protected override void DrawUnrotated(CanvasDrawingSession ds)
    {
        using var shape = Geometry(ds);
        // A sticker is the icon itself (outlined or solid, by Filled), not a line around it.
        if (Kind == ShapeKind.Sticker)
            ds.FillGeometry(shape, Color);
        else
            ShapeDrawing.Draw(ds, shape, Color, Thickness, Filled);
    }

    protected override bool HitTestUnrotated(Vector2 point, float tolerance)
    {
        // Stickers are small and mostly holes, so they're grabbable anywhere in their box.
        if (Kind == ShapeKind.Sticker)
            return Shape.Inflate(tolerance).Contains(point.ToPoint());
        using var shape = Geometry(CanvasDevice.GetSharedDevice());
        return ShapeDrawing.HitTest(shape, point, Thickness, tolerance, Filled);
    }
}

/// <summary>
/// A polygon of any number of corners, clicked out one corner at a time. Until it's finished
/// (<see cref="Closed"/>), it's drawn as the open line so far.
/// </summary>
internal sealed class PolygonAnnotation : Annotation, IFillable
{
    public PolygonAnnotation(Vector2 first, Color color, int weight, float unit)
        : base(color, weight, unit)
    {
        Points.Add(first);
    }

    public List<Vector2> Points { get; private set; } = new();

    public bool Closed { get; set; }

    public bool Filled { get; set; }

    public float Thickness => (1 + Weight) * Unit;

    public override Rect Frame
    {
        get
        {
            float minX = Points.Min(p => p.X), minY = Points.Min(p => p.Y);
            var box = new Rect(minX, minY, Points.Max(p => p.X) - minX, Points.Max(p => p.Y) - minY);
            return box.Inflate(Thickness / 2);
        }
    }

    private CanvasGeometry Geometry(ICanvasResourceCreator creator)
    {
        using var path = new CanvasPathBuilder(creator);
        path.BeginFigure(Points[0]);
        foreach (var point in Points.Skip(1))
            path.AddLine(point);
        path.EndFigure(Closed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(path);
    }

    protected override void DrawUnrotated(CanvasDrawingSession ds)
    {
        if (Points.Count == 1)
        {
            ds.FillCircle(Points[0], Thickness / 2, Color);
            return;
        }
        using var shape = Geometry(ds);
        // Only a finished polygon is filled; while it's being clicked out, it's just the line.
        ShapeDrawing.Draw(ds, shape, Color, Thickness, Filled && Closed);
    }

    protected override bool HitTestUnrotated(Vector2 point, float tolerance)
    {
        if (Points.Count == 1)
            return Vector2.Distance(point, Points[0]) <= Thickness / 2 + tolerance;
        using var shape = Geometry(CanvasDevice.GetSharedDevice());
        return ShapeDrawing.HitTest(shape, point, Thickness, tolerance, Filled && Closed);
    }

    protected override void Offset(Vector2 delta)
    {
        for (int i = 0; i < Points.Count; i++)
            Points[i] += delta;
    }

    /// <summary>Turns the corners themselves, so the angle stays zero.</summary>
    public override void RotateBy(float radians, Vector2 around)
    {
        var turn = Matrix3x2.CreateRotation(radians, around);
        for (int i = 0; i < Points.Count; i++)
            Points[i] = Vector2.Transform(Points[i], turn);
    }

    /// <summary>Every corner, once it's finished.</summary>
    protected override IReadOnlyList<Vector2> UnrotatedHandles => Closed ? Points : [];

    protected override void MoveUnrotatedHandle(int index, Vector2 position) => Points[index] = position;

    protected override void OnCloned()
    {
        // A copy for undo must not share the list of points with the original.
        Points = new List<Vector2>(Points);
    }
}
