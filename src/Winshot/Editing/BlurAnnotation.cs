using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;

namespace Winshot.Editing;

/// <summary>
/// Obscures a region of the screenshot. It samples only the original image, so it hides
/// what was captured without smearing annotations, and it never extends the canvas.
/// </summary>
internal sealed class BlurAnnotation : BoxAnnotation, IDisposable
{
    private readonly ICanvasImage _source;
    private readonly Rect _imageBounds;

    public BlurAnnotation(Vector2 start, ICanvasImage source, Rect imageBounds, bool pixelate, int weight, float unit)
        : base(start, Microsoft.UI.Colors.Transparent, weight, unit)
    {
        _source = source;
        _imageBounds = imageBounds;
        Pixelate = pixelate;
    }

    /// <summary>
    /// Pixelate averages into large blocks and is the safe choice for redaction; a light
    /// Gaussian blur over text can sometimes be partially reversed.
    /// </summary>
    public bool Pixelate { get; set; }

    public override bool IsAreaEffect => true;

    private float BlockSize => (4 + Weight * 2) * Unit;

    private float BlurAmount => (3 + Weight * 1.5f) * Unit;

    /// <summary>The part of the box that covers the image; there is nothing to blur elsewhere.</summary>
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

    public override void Draw(CanvasDrawingSession ds)
    {
        var region = Region;
        if (region.IsEmpty || region.Width < 1 || region.Height < 1)
            return;

        if (Pixelate)
            DrawPixelated(ds, region);
        else
            DrawBlurred(ds, region);
    }

    private void DrawBlurred(CanvasDrawingSession ds, Rect region)
    {
        // Repeat the edge pixels outward before blurring, so the region's borders don't pull
        // in transparency and fade.
        using var crop = new CropEffect { Source = _source, SourceRectangle = region };
        using var extend = new BorderEffect { Source = crop, ExtendX = CanvasEdgeBehavior.Clamp, ExtendY = CanvasEdgeBehavior.Clamp };
        using var blur = new GaussianBlurEffect { Source = extend, BlurAmount = BlurAmount, Optimization = EffectOptimization.Quality };
        ds.DrawImage(blur, region, region);
    }

    private void DrawPixelated(CanvasDrawingSession ds, Rect region)
    {
        // Downscale into a small real bitmap, then draw it back up with nearest-neighbor.
        // (Chaining two scale effects doesn't work: Direct2D folds them into one transform.)
        // The high-quality downscale averages each block's area rather than sampling one pixel.
        int columns = Math.Max(1, (int)Math.Ceiling(region.Width / BlockSize));
        int rows = Math.Max(1, (int)Math.Ceiling(region.Height / BlockSize));
        var key = (region, columns, rows);
        if (_pixels is null || _pixelsKey != key)
        {
            _pixels?.Dispose();
            _pixels = new CanvasRenderTarget(ds, columns, rows, 96);
            using (var small = _pixels.CreateDrawingSession())
            {
                small.DrawImage(_source, new Rect(0, 0, columns, rows), region, 1, CanvasImageInterpolation.HighQualityCubic);
            }
            _pixelsKey = key;
        }

        // Stretching the grid over the region makes blocks just under BlockSize, and keeps
        // them aligned to the region's edges.
        ds.DrawImage(_pixels, region, new Rect(0, 0, columns, rows), 1, CanvasImageInterpolation.NearestNeighbor);
    }

    private CanvasRenderTarget? _pixels;
    private (Rect, int, int) _pixelsKey;

    protected override void OnCloned()
    {
        // The cached blocks are rebuilt lazily; sharing them would let one copy dispose the other's.
        _pixels = null;
    }

    public void Dispose() => _pixels?.Dispose();

    public override bool HitTest(Vector2 point, float tolerance) =>
        Region.Inflate(tolerance).Contains(point.ToPoint());
}
