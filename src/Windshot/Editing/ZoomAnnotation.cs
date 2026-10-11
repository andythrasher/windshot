using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace Windshot.Editing;

/// <summary>
/// In a recording: an area to zoom in to. During its time the view eases in until the area
/// fills the video, then back out; its fades are how long the zooming in and out take. Like a
/// camera move, it changes the whole picture, annotations included (see
/// <see cref="Document.Camera"/>).
/// </summary>
internal sealed class ZoomAnnotation : BoxAnnotation
{
    /// <summary>The closest it goes: an area an eighth of the video's width.</summary>
    public const float MaxScale = 8;

    public ZoomAnnotation(Vector2 start, float unit)
        : base(start, Microsoft.UI.Colors.Transparent, Annotation.MinWeight, unit)
    {
        FadeIn = FadeOut = FadeLength.Slow;
    }

    public override bool IsAreaEffect => true;

    public override bool IsEffect => true;

    public override bool CanRotate => false;

    public override Rect Frame => Shape;

    /// <summary>Not drawn: it moves the view instead (see <see cref="Document.Camera"/>).</summary>
    protected override void DrawUnrotated(CanvasDrawingSession ds)
    {
    }

    protected override bool HitTestUnrotated(Vector2 point, float tolerance) =>
        Shape.Inflate(tolerance).Contains(point.ToPoint());

    /// <summary>
    /// What fills the video when fully zoomed in: the area, widened or heightened to the video's
    /// shape around its middle, and kept inside the video.
    /// </summary>
    public Rect Framing(Rect video)
    {
        var area = Shape;
        double aspect = video.Width / video.Height;
        double width = Math.Max(area.Width, area.Height * aspect);
        width = Math.Clamp(width, video.Width / MaxScale, video.Width);
        double height = width / aspect;
        double x = area.X + area.Width / 2 - width / 2, y = area.Y + area.Height / 2 - height / 2;
        x = Math.Clamp(x, video.X, video.Right - width);
        y = Math.Clamp(y, video.Y, video.Bottom - height);
        return new Rect(x, y, width, height);
    }
}
