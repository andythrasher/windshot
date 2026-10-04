using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windshot.Capture;

namespace Windshot.Editing;

/// <summary>
/// The screenshot is one layer at (0,0) on an unbounded canvas, not the canvas itself.
/// The canvas size is derived from the image plus every annotation, which is what lets
/// shapes and text extend past the original capture ("reverse crop").
/// </summary>
internal sealed class Document : IDisposable
{
    /// <summary>Breathing room added around annotations that stick out past the image.</summary>
    public const float ExpansionMargin = 24;

    public Document(CapturedImage capture)
    {
        // 96 DPI makes one DIP equal one image pixel, so document units are image pixels.
        Image = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), capture.Pixels,
            capture.Width, capture.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        SourceScale = capture.Scale;
        _edgeColors = new EdgeColors(capture.Pixels, capture.Width, capture.Height);
    }

    private readonly EdgeColors _edgeColors;

    public CanvasBitmap Image { get; }

    /// <summary>Device pixels per DIP on the monitor the capture came from.</summary>
    public double SourceScale { get; }

    public List<Annotation> Annotations { get; } = new();

    public Rect ImageBounds => new(0, 0, Image.SizeInPixels.Width, Image.SizeInPixels.Height);

    /// <summary>Explicit crop. When null, the canvas auto-fits the image and all annotations.</summary>
    public Rect? CropOverride { get; set; }

    /// <summary>How the screenshot is scaled when the editor zooms; set by the editor before drawing.</summary>
    public CanvasImageInterpolation ImageInterpolation { get; set; } = CanvasImageInterpolation.Linear;

    /// <summary>Optional "beautify" style. When set, it's part of what you see and export.</summary>
    public Backdrop? Backdrop { get; set; }

    private float Unit => (float)SourceScale;

    private float CornerRadius => Backdrop is null ? 0 : Backdrop.CornerRadius * Unit;

    /// <summary>The whole canvas: content plus backdrop padding, if any. This is what's exported.</summary>
    public Rect Bounds => Backdrop is null
        ? ContentBounds
        : ContentBounds.Inflate(Backdrop.Padding * Unit).RoundOut();

    /// <summary>The image plus any annotations that stick out past it.</summary>
    public Rect ContentBounds
    {
        get
        {
            if (CropOverride is Rect crop)
                return crop;

            var image = ImageBounds;
            var bounds = image;
            foreach (var annotation in Annotations.Where(a => a.ExtendsCanvas))
            {
                var a = annotation.Bounds;
                if (!image.ContainsRect(a))
                    bounds = bounds.UnionWith(a.Inflate(ExpansionMargin));
            }
            return bounds.RoundOut();
        }
    }

    /// <summary>
    /// Fills for canvas added around the image, so it continues the screenshot's background.
    /// Each side the canvas grew toward gets the dominant color of the image edge next to it,
    /// preferring the stretch of edge beside whatever overhangs it ("nearby"), then the whole
    /// edge. Sides with no clear background color stay transparent.
    /// </summary>
    /// <param name="complete">True when every side the canvas grew toward got a fill.</param>
    private List<(Rect Band, Windows.UI.Color Color)> ExpansionFills(out bool complete)
    {
        var content = ContentBounds;
        var image = ImageBounds;
        var overhanging = Annotations.Where(a => a.ExtendsCanvas).Select(a => a.Bounds).Where(b => !b.IsEmpty).ToList();
        var fills = new List<(Rect, Windows.UI.Color)>();
        bool allFilled = true;

        void Fill(Sides side, Rect band, bool vertical, Func<Rect, bool> overhangs)
        {
            if (band.Width <= 0 || band.Height <= 0)
                return;

            // The span of edge beside what overhangs this side, with some margin.
            int length = (int)(vertical ? image.Height : image.Width);
            var near = overhanging.Where(overhangs).ToList();
            Windows.UI.Color? color = null;
            if (near.Count > 0)
            {
                double lo = near.Min(b => vertical ? b.Top : b.Left);
                double hi = near.Max(b => vertical ? b.Bottom : b.Right);
                double margin = Math.Max(40, length * 0.1);
                color = _edgeColors.Dominant(side, (int)(lo - margin), (int)(hi + margin));
            }
            color ??= _edgeColors.Dominant(side, 0, length);

            if (color is Windows.UI.Color c)
                fills.Add((band, c));
            else
                allFilled = false;
        }

        // Left and right bands run the full height, so they own the corners.
        Fill(Sides.Left, new Rect(content.Left, content.Top, image.Left - content.Left, content.Height), true, b => b.Left < image.Left);
        Fill(Sides.Right, new Rect(image.Right, content.Top, content.Right - image.Right, content.Height), true, b => b.Right > image.Right);
        Fill(Sides.Top, new Rect(image.Left, content.Top, image.Width, image.Top - content.Top), false, b => b.Top < image.Top);
        Fill(Sides.Bottom, new Rect(image.Left, image.Bottom, image.Width, content.Bottom - image.Bottom), false, b => b.Bottom > image.Bottom);
        complete = allFilled;
        return fills;
    }

    /// <summary>
    /// Paints in layers: the backdrop if any; then the card (expansion fill, image, and what
    /// marks the image itself: blurs, highlights, spotlight dimming), clipped to rounded
    /// corners when beautified; then every other annotation on top, unclipped, so arrows and
    /// text stay crisp and can reach out onto the backdrop.
    /// </summary>
    /// <param name="skip">An annotation the editor is showing some other way, e.g. text being typed.</param>
    public void Render(CanvasDrawingSession ds, Annotation? skip = null)
    {
        // The card (what beautify rounds and shadows) covers the expansion only when every
        // added side got a fill; otherwise it's just the image, and partial fills sit outside it.
        var fills = ExpansionFills(out bool complete);
        bool fillsInCard = complete && fills.Count > 0;
        var card = fillsInCard ? ContentBounds : ImageBounds;
        if (Backdrop is not null)
            DrawBackdrop(ds, Backdrop, card);
        if (!fillsInCard)
            DrawFills(ds, fills);

        using (var rounded = CornerRadius > 0 ? CanvasGeometry.CreateRoundedRectangle(ds, card, CornerRadius, CornerRadius) : null)
        using (rounded is null ? null : ds.CreateLayer(1, rounded))
        {
            if (fillsInCard)
                DrawFills(ds, fills);
            ds.DrawImage(Image, ImageBounds, ImageBounds, 1, ImageInterpolation);

            foreach (var blur in Annotations.OfType<BlurAnnotation>())
                blur.Draw(ds);
            foreach (var highlight in Annotations.OfType<HighlighterAnnotation>())
                highlight.Draw(ds);
            SpotlightAnnotation.DrawDimming(ds, card, Annotations.OfType<SpotlightAnnotation>().ToList());
        }

        int step = 0;
        foreach (var annotation in Annotations.Where(a => a is { IsAreaEffect: false } and not HighlighterAnnotation))
        {
            if (annotation is StepAnnotation marker)
                marker.Number = ++step;
            if (annotation != skip)
                annotation.Draw(ds);
        }
    }

    private static void DrawFills(CanvasDrawingSession ds, List<(Rect Band, Windows.UI.Color Color)> fills)
    {
        // Bands share edges with the image; antialiasing would leave hairline seams.
        var mode = ds.Antialiasing;
        ds.Antialiasing = CanvasAntialiasing.Aliased;
        foreach (var (band, color) in fills)
            ds.FillRectangle(band, color);
        ds.Antialiasing = mode;
    }

    private void DrawBackdrop(CanvasDrawingSession ds, Backdrop backdrop, Rect card)
    {
        var bounds = Bounds;
        using (var gradient = new CanvasLinearGradientBrush(ds, backdrop.From, backdrop.To)
        {
            StartPoint = new Vector2((float)bounds.Left, (float)bounds.Top),
            EndPoint = new Vector2((float)bounds.Right, (float)bounds.Bottom),
        })
        {
            ds.FillRectangle(bounds, gradient);
        }

        // A soft shadow under the card, cast slightly downward.
        using var shape = new CanvasCommandList(ds);
        using (var s = shape.CreateDrawingSession())
            s.FillRoundedRectangle(card, CornerRadius, CornerRadius, Microsoft.UI.Colors.Black);
        using var shadow = new ShadowEffect
        {
            Source = shape,
            BlurAmount = 12 * Unit,
            ShadowColor = Windows.UI.Color.FromArgb(110, 0, 0, 0),
        };
        ds.DrawImage(shadow, 0, 6 * Unit);
    }

    /// <param name="includeAreaEffects">
    /// Area effects fill their whole region; leaving them out lets you start an arrow inside
    /// a blur or spotlight instead of accidentally grabbing it.
    /// </param>
    public Annotation? HitTest(Vector2 point, float tolerance, bool includeAreaEffects)
    {
        // Topmost first: regular annotations paint above area effects.
        var ordered = Annotations.Where(a => !a.IsAreaEffect).Reverse()
            .Concat(includeAreaEffects ? Annotations.Where(a => a.IsAreaEffect).Reverse() : []);
        return ordered.FirstOrDefault(a => a.HitTest(point, tolerance));
    }

    /// <summary>Renders the full canvas to a PNG. Without a backdrop, area outside the image is transparent.</summary>
    public async Task<Windows.Storage.Streams.InMemoryRandomAccessStream> EncodePngAsync()
    {
        var bounds = Bounds;
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(),
            (float)bounds.Width, (float)bounds.Height, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Microsoft.UI.Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(-(float)bounds.X, -(float)bounds.Y);
            // Exports are 1:1, so copy pixels exactly regardless of the editor's zoom.
            var interpolation = ImageInterpolation;
            ImageInterpolation = CanvasImageInterpolation.NearestNeighbor;
            Render(ds);
            ImageInterpolation = interpolation;
        }

        var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        return stream;
    }

    public void Dispose()
    {
        Image.Dispose();
        foreach (var annotation in Annotations.OfType<IDisposable>())
            annotation.Dispose();
    }
}
