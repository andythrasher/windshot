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

/// <summary>A rectangle, ellipse or triangle, dragged out as a box.</summary>
internal sealed class ShapeAnnotation : BoxAnnotation, IFillable
{
    public ShapeAnnotation(Vector2 start, ShapeKind kind, Color color, int weight, float unit)
        : base(start, color, weight, unit)
    {
        Kind = kind;
    }

    /// <summary>Rectangle, Ellipse or Triangle; they share the box, so one can become another.</summary>
    public ShapeKind Kind { get; set; }

    public bool Filled { get; set; }

    public float Thickness => (1 + Weight) * Unit;

    public override Rect Bounds => Shape.Inflate(Thickness / 2);

    private CanvasGeometry Geometry(ICanvasResourceCreator creator)
    {
        var box = Shape;
        switch (Kind)
        {
            case ShapeKind.Ellipse:
                return CanvasGeometry.CreateEllipse(creator, (float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2),
                    (float)box.Width / 2, (float)box.Height / 2);
            case ShapeKind.Triangle:
                // Pointing up, with its base along the bottom of the box.
                float left = (float)box.Left, right = (float)box.Right, top = (float)box.Top, bottom = (float)box.Bottom;
                return CanvasGeometry.CreatePolygon(creator, [new((left + right) / 2, top), new(right, bottom), new(left, bottom)]);
            default:
                return CanvasGeometry.CreateRoundedRectangle(creator, box, Thickness / 2, Thickness / 2);
        }
    }

    public override void Draw(CanvasDrawingSession ds)
    {
        using var shape = Geometry(ds);
        ShapeDrawing.Draw(ds, shape, Color, Thickness, Filled);
    }

    public override bool HitTest(Vector2 point, float tolerance)
    {
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

    public override Rect Bounds
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

    public override void Draw(CanvasDrawingSession ds)
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

    public override bool HitTest(Vector2 point, float tolerance)
    {
        if (Points.Count == 1)
            return Vector2.Distance(point, Points[0]) <= Thickness / 2 + tolerance;
        using var shape = Geometry(CanvasDevice.GetSharedDevice());
        return ShapeDrawing.HitTest(shape, point, Thickness, tolerance, Filled && Closed);
    }

    public override void MoveBy(Vector2 delta)
    {
        for (int i = 0; i < Points.Count; i++)
            Points[i] += delta;
    }

    /// <summary>Every corner, once it's finished.</summary>
    public override IReadOnlyList<Vector2> Handles => Closed ? Points : [];

    public override void MoveHandle(int index, Vector2 position) => Points[index] = position;

    protected override void OnCloned()
    {
        // A copy for undo must not share the list of points with the original.
        Points = new List<Vector2>(Points);
    }
}
