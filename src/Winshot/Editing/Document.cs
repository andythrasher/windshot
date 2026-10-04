using System.Numerics;
using Microsoft.Graphics.Canvas;
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

    public Rect Bounds
    {
        get
        {
            if (CropOverride is Rect crop)
                return crop;

            var image = ImageBounds;
            var bounds = image;
            foreach (var annotation in Annotations)
            {
                var a = annotation.Bounds;
                if (!image.ContainsRect(a))
                    bounds = bounds.UnionWith(a.Inflate(ExpansionMargin));
            }
            return bounds.RoundOut();
        }
    }

    /// <param name="skip">An annotation the editor is showing some other way, e.g. text being typed.</param>
    public void Render(CanvasDrawingSession ds, Annotation? skip = null)
    {
        ds.DrawImage(Image, 0, 0);
        foreach (var annotation in Annotations)
        {
            if (annotation != skip)
                annotation.Draw(ds);
        }
    }

    public Annotation? HitTest(Vector2 point, float tolerance)
    {
        for (int i = Annotations.Count - 1; i >= 0; i--)
        {
            if (Annotations[i].HitTest(point, tolerance))
                return Annotations[i];
        }
        return null;
    }

    /// <summary>Renders the full canvas to a PNG. Area outside the image is transparent.</summary>
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
