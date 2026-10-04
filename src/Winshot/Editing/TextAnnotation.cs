using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI;

namespace Winshot.Editing;

/// <summary>Bold text with a contrasting outline so it reads on any background.</summary>
internal sealed class TextAnnotation : Annotation, IDisposable
{
    public const string FontFamily = "Segoe UI Variable Display";
    public static readonly Windows.UI.Text.FontWeight FontWeight = FontWeights.SemiBold;

    private static readonly CanvasStrokeStyle RoundJoin = new() { LineJoin = CanvasLineJoin.Round };

    private CanvasTextLayout? _layout;
    private string? _layoutText;
    private float _layoutSize;

    public TextAnnotation(Vector2 position, Color color, int weight, float unit)
        : base(color, weight, unit)
    {
        Position = position;
    }

    /// <summary>Top-left of the first line.</summary>
    public Vector2 Position { get; set; }

    public string Text { get; set; } = "";

    public float FontSize => (10 + Weight * 4) * Unit;

    private float OutlineWidth => FontSize * 0.14f;

    private Color OutlineColor => Color.Contrasting();

    private CanvasTextLayout Layout
    {
        get
        {
            if (_layout is null || _layoutText != Text || _layoutSize != FontSize)
            {
                _layout?.Dispose();
                var format = new CanvasTextFormat
                {
                    FontFamily = FontFamily,
                    FontSize = FontSize,
                    FontWeight = FontWeight,
                    WordWrapping = CanvasWordWrapping.NoWrap,
                };
                _layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), Text, format, 0, 0);
                _layoutText = Text;
                _layoutSize = FontSize;
            }
            return _layout;
        }
    }

    public override Rect Bounds
    {
        get
        {
            var layout = Layout.LayoutBounds;
            return new Rect(Position.X + layout.X, Position.Y + layout.Y, layout.Width, layout.Height)
                .Inflate(OutlineWidth);
        }
    }

    public override void Draw(CanvasDrawingSession ds) => Draw(ds, outlineOnly: false);

    /// <param name="outlineOnly">
    /// Draws just the outline, for use behind the live text box while typing, so editing
    /// looks the same as the finished text.
    /// </param>
    public void Draw(CanvasDrawingSession ds, bool outlineOnly)
    {
        if (string.IsNullOrEmpty(Text))
            return;

        using var geometry = CanvasGeometry.CreateText(Layout);
        ds.DrawGeometry(geometry, Position, OutlineColor, OutlineWidth * 2, RoundJoin);
        if (!outlineOnly)
            ds.FillGeometry(geometry, Position, Color);
    }

    public override bool HitTest(Vector2 point, float tolerance) =>
        Bounds.Inflate(tolerance).Contains(point.ToPoint());

    public override void MoveBy(Vector2 delta) => Position += delta;

    protected override void OnCloned()
    {
        // The layout is rebuilt lazily; sharing it would let one copy dispose the other's.
        _layout = null;
        _layoutText = null;
    }

    public void Dispose() => _layout?.Dispose();
}
