using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// Keeps a region bright while the rest dims. Spotlights work together: one dimming layer
/// with a hole for every visible spotlight, applied where the topmost one sits in the stack
/// (see <see cref="Dim"/>); separately, each would darken the others' bright areas.
/// </summary>
internal sealed class SpotlightAnnotation : BoxAnnotation
{
    public SpotlightAnnotation(Vector2 start, int weight, float unit)
        : base(start, Microsoft.UI.Colors.Transparent, weight, unit)
    {
    }

    public override bool IsAreaEffect => true;

    public override bool IsEffect => true;

    /// <summary>How dark the area outside spotlights gets.</summary>
    public float DimOpacity => Math.Clamp(0.2f + Weight * 0.06f, 0, 0.85f);

    private float CornerRadius => 8 * Unit;

    public override Rect Frame => Shape;

    /// <summary>Drawn by <see cref="Dim"/>, as part of the layer stack.</summary>
    protected override void DrawUnrotated(CanvasDrawingSession ds)
    {
    }

    protected override bool HitTestUnrotated(Vector2 point, float tolerance) =>
        Shape.Inflate(tolerance).Contains(point.ToPoint());

    /// <summary>
    /// Everything beneath, dimmed within the card (the screenshot plus any filled expansion)
    /// outside all the given spotlights. Transparent canvas outside the card stays transparent
    /// rather than turning grey on export.
    /// </summary>
    public static ICanvasImage Dim(LayerContext context, ICanvasImage below, IReadOnlyList<SpotlightAnnotation> spotlights)
    {
        var result = context.NewList();
        using var s = result.CreateDrawingSession();
        s.DrawImage(below);
        if (spotlights.Count == 0)
            return result;

        // Rounded like the card when beautified, so the corners don't darken the backdrop.
        var dimmed = context.CardShape(s) ?? CanvasGeometry.CreateRectangle(s, context.Card);
        foreach (var spot in spotlights)
        {
            var region = spot.Shape;
            if (region.Width < 1 || region.Height < 1)
                continue;
            using var hole = CanvasGeometry.CreateRoundedRectangle(s, region, spot.CornerRadius, spot.CornerRadius);
            var next = dimmed.CombineWith(hole, spot.Rotation, CanvasGeometryCombine.Exclude);
            dimmed.Dispose();
            dimmed = next;
        }

        // One darkness for the whole layer: the darkest any spotlight asks for.
        float opacity = spotlights.Max(spot => spot.DimOpacity);
        s.FillGeometry(dimmed, Color.FromArgb((byte)(255 * opacity), 0, 0, 0));
        dimmed.Dispose();
        return result;
    }
}
