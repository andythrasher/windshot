using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace Winshot.Editing;

/// <summary>
/// Keeps a region bright while the rest of the screenshot dims. The dimming is drawn once
/// for all spotlights together (see <see cref="DrawDimming"/>), so several spotlights
/// combine into one layer with several holes.
/// </summary>
internal sealed class SpotlightAnnotation : BoxAnnotation
{
    private readonly Rect _imageBounds;

    public SpotlightAnnotation(Vector2 start, Rect imageBounds, int weight, float unit)
        : base(start, Microsoft.UI.Colors.Transparent, weight, unit)
    {
        _imageBounds = imageBounds;
    }

    public override bool IsAreaEffect => true;

    /// <summary>How dark the area outside spotlights gets.</summary>
    public float DimOpacity => Math.Clamp(0.2f + Weight * 0.06f, 0, 0.85f);

    private float CornerRadius => 8 * Unit;

    private Rect Region
    {
        get
        {
            var region = Shape;
            region.Intersect(_imageBounds);
            return region;
        }
    }

    public override Rect Bounds => Region.IsEmpty ? Rect.Empty : Region;

    /// <summary>Nothing of its own to draw; the document draws the shared dimming layer.</summary>
    public override void Draw(CanvasDrawingSession ds)
    {
    }

    public override bool HitTest(Vector2 point, float tolerance) =>
        Region.Inflate(tolerance).Contains(point.ToPoint());

    /// <summary>
    /// Dims the screenshot (<paramref name="card"/>: the image plus any filled expansion)
    /// everywhere outside the given spotlights. Transparent canvas outside the card stays
    /// transparent rather than turning grey on export.
    /// </summary>
    public static void DrawDimming(CanvasDrawingSession ds, Rect card, IReadOnlyList<SpotlightAnnotation> spotlights)
    {
        if (spotlights.Count == 0)
            return;

        var dimmed = CanvasGeometry.CreateRectangle(ds, card);
        foreach (var spot in spotlights)
        {
            var region = spot.Region;
            if (region.IsEmpty)
                continue;
            using var hole = CanvasGeometry.CreateRoundedRectangle(ds, region, spot.CornerRadius, spot.CornerRadius);
            var next = dimmed.CombineWith(hole, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            dimmed.Dispose();
            dimmed = next;
        }

        // One opacity for the whole layer: the darkest any spotlight asks for.
        float opacity = spotlights.Max(s => s.DimOpacity);
        ds.FillGeometry(dimmed, Color.FromArgb((byte)(255 * opacity), 0, 0, 0));
        dimmed.Dispose();
    }
}
