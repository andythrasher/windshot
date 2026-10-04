using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Winshot.Capture;

namespace Winshot.Editing;

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
    }

    public CanvasBitmap Image { get; }

    /// <summary>Device pixels per DIP on the monitor the capture came from.</summary>
    public double SourceScale { get; }

    public List<Annotation> Annotations { get; } = new();

    public Rect ImageBounds => new(0, 0, Image.SizeInPixels.Width, Image.SizeInPixels.Height);

    /// <summary>Explicit crop. When null, the canvas auto-fits the image and all annotations.</summary>
    public Rect? CropOverride { get; set; }

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
    /// Paints in layers: the backdrop if any, the image, then what marks the image itself
    /// (blurs, highlights, then the spotlight dimming), then every other annotation on top,
    /// so arrows and text stay crisp and bright.
    /// </summary>
    /// <param name="skip">An annotation the editor is showing some other way, e.g. text being typed.</param>
    public void Render(CanvasDrawingSession ds, Annotation? skip = null)
    {
        if (Backdrop is not null)
            DrawBackdrop(ds, Backdrop);
        DrawImage(ds);

        foreach (var blur in Annotations.OfType<BlurAnnotation>())
            blur.Draw(ds);
        foreach (var highlight in Annotations.OfType<HighlighterAnnotation>())
            highlight.Draw(ds);
        SpotlightAnnotation.DrawDimming(ds, ImageBounds, CornerRadius, Annotations.OfType<SpotlightAnnotation>().ToList());

        int step = 0;
        foreach (var annotation in Annotations.Where(a => a is { IsAreaEffect: false } and not HighlighterAnnotation))
        {
            if (annotation is StepAnnotation marker)
                marker.Number = ++step;
            if (annotation != skip)
                annotation.Draw(ds);
        }
    }

    private void DrawBackdrop(CanvasDrawingSession ds, Backdrop backdrop)
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

        // A soft shadow under the screenshot itself, cast slightly downward.
        using var shape = new CanvasCommandList(ds);
        using (var s = shape.CreateDrawingSession())
            s.FillRoundedRectangle(ImageBounds, CornerRadius, CornerRadius, Microsoft.UI.Colors.Black);
        using var shadow = new ShadowEffect
        {
            Source = shape,
            BlurAmount = 12 * Unit,
            ShadowColor = Windows.UI.Color.FromArgb(110, 0, 0, 0),
        };
        ds.DrawImage(shadow, 0, 6 * Unit);
    }

    private void DrawImage(CanvasDrawingSession ds)
    {
        if (CornerRadius <= 0)
        {
            ds.DrawImage(Image, 0, 0);
            return;
        }
        using var brush = new CanvasImageBrush(ds, Image);
        ds.FillRoundedRectangle(ImageBounds, CornerRadius, CornerRadius, brush);
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
            Render(ds);
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
