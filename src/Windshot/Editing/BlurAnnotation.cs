using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;

namespace Windshot.Editing;

/// <summary>
/// Obscures a region: whatever is beneath it in the layer stack (the screenshot, and any
/// annotations below it), pixelated or blurred. It never extends the canvas.
/// </summary>
internal sealed class BlurAnnotation : BoxAnnotation
{
    public BlurAnnotation(Vector2 start, bool pixelate, int weight, float unit)
        : base(start, Microsoft.UI.Colors.Transparent, weight, unit)
    {
        Pixelate = pixelate;
    }

    /// <summary>
    /// Pixelate averages into large blocks and is the safe choice for redaction; a light
    /// Gaussian blur over text can sometimes be partially reversed.
    /// </summary>
    public bool Pixelate { get; set; }

    public override bool IsAreaEffect => true;

    public override bool IsEffect => true;

    private float BlockSize => (4 + Weight * 2) * Unit;

    private float BlurAmount => (3 + Weight * 1.5f) * Unit;

    public override Rect Frame => Shape;

    /// <summary>Drawn by <see cref="ApplyTo"/>, as part of the layer stack.</summary>
    protected override void DrawUnrotated(CanvasDrawingSession ds)
    {
    }

    public override ICanvasImage ApplyTo(LayerContext context, ICanvasImage below)
    {
        var region = Shape;
        if (region.Width < 1 || region.Height < 1)
            return below;
        // Turned, the region is obscured in its own frame: what's beneath, turned the other way.
        var rotation = Rotation;
        var source = below;
        if (Angle != 0 && Matrix3x2.Invert(rotation, out var toFrame))
            source = context.Own(new Transform2DEffect { Source = below, TransformMatrix = toFrame });
        var obscured = Pixelate ? Pixelated(context, source, region) : Blurred(context, source, region);

        // What's beneath is drawn with a hole where the region is, and the obscured pixels go
        // in the hole: they replace the originals outright, so nothing can show through.
        // (A "copy" composite would do the same in one step, but replayed from a command
        // list it wipes everything outside the region too.)
        var result = context.NewList();
        using (var s = result.CreateDrawingSession())
        {
            var everywhere = new Rect(-1e6, -1e6, 2e6, 2e6);
            using (var all = CanvasGeometry.CreateRectangle(s, everywhere))
            using (var hole = CanvasGeometry.CreateRectangle(s, region))
            using (var around = all.CombineWith(hole, rotation, CanvasGeometryCombine.Exclude))
            using (s.CreateLayer(1, around))
            {
                s.DrawImage(below);
            }
            s.Transform = rotation;
            s.DrawImage(obscured.Image, region, obscured.Source, 1, obscured.Interpolation);
        }
        return result;
    }

    private (ICanvasImage Image, Rect Source, CanvasImageInterpolation Interpolation) Blurred(LayerContext context, ICanvasImage below, Rect region)
    {
        // Repeat the edge pixels outward before blurring, so the region's borders don't pull
        // in what's around it (or transparency) and fade.
        var crop = context.Own(new CropEffect { Source = below, SourceRectangle = region });
        var extend = context.Own(new BorderEffect { Source = crop, ExtendX = CanvasEdgeBehavior.Clamp, ExtendY = CanvasEdgeBehavior.Clamp });
        var blur = context.Own(new GaussianBlurEffect { Source = extend, BlurAmount = BlurAmount, Optimization = EffectOptimization.Quality });
        return (blur, region, CanvasImageInterpolation.Linear);
    }

    private (ICanvasImage Image, Rect Source, CanvasImageInterpolation Interpolation) Pixelated(LayerContext context, ICanvasImage below, Rect region)
    {
        // Downscale into a small real bitmap, then draw it back up with nearest-neighbor.
        // (Chaining two scale effects doesn't work: Direct2D folds them into one transform.)
        // The high-quality downscale averages each block's area rather than sampling one pixel.
        int columns = Math.Max(1, (int)Math.Ceiling(region.Width / BlockSize));
        int rows = Math.Max(1, (int)Math.Ceiling(region.Height / BlockSize));
        var blocks = context.Own(new CanvasRenderTarget(context.Device, columns, rows, 96));
        using (var s = blocks.CreateDrawingSession())
        {
            s.Clear(Microsoft.UI.Colors.Transparent);
            s.DrawImage(below, new Rect(0, 0, columns, rows), region, 1, CanvasImageInterpolation.HighQualityCubic);
        }
        // Stretching the grid over the region makes blocks just under BlockSize, and keeps
        // them aligned to the region's edges.
        return (blocks, new Rect(0, 0, columns, rows), CanvasImageInterpolation.NearestNeighbor);
    }

    protected override bool HitTestUnrotated(Vector2 point, float tolerance) =>
        Shape.Inflate(tolerance).Contains(point.ToPoint());
}
