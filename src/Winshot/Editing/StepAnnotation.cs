using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI;

namespace Winshot.Editing;

/// <summary>A numbered badge for marking steps: 1, 2, 3… in the order they were placed.</summary>
internal sealed class StepAnnotation : Annotation
{
    public StepAnnotation(Vector2 center, Color color, int weight, float unit)
        : base(color, weight, unit)
    {
        Center = center;
    }

    public Vector2 Center { get; set; }

    /// <summary>
    /// Assigned by the document from drawing order rather than stored, so deleting a step
    /// renumbers the ones after it.
    /// </summary>
    public int Number { get; set; } = 1;

    public float Radius => (8 + Weight * 2.5f) * Unit;

    private float RingWidth => Radius * 0.14f;

    public override Rect Bounds
    {
        get
        {
            float r = Radius + RingWidth;
            return new Rect(Center.X - r, Center.Y - r, r * 2, r * 2);
        }
    }

    public override void Draw(CanvasDrawingSession ds)
    {
        var contrast = Color.Contrasting();
        ds.FillCircle(Center, Radius + RingWidth, contrast);
        ds.FillCircle(Center, Radius, Color);

        string label = Number.ToString();
        using var format = new CanvasTextFormat
        {
            FontFamily = TextAnnotation.FontFamily,
            FontWeight = FontWeights.Bold,
            // Shrink a little for two or more digits so they stay inside the circle.
            FontSize = Radius * (label.Length > 1 ? 0.95f : 1.2f),
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        ds.DrawText(label, new Rect(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2), contrast, format);
    }

    public override bool HitTest(Vector2 point, float tolerance) =>
        Vector2.Distance(point, Center) <= Radius + RingWidth + tolerance;

    public override void MoveBy(Vector2 delta) => Center += delta;
}
