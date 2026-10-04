using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// Bold text that reads on any background: either outlined in a contrasting color, or
/// (<see cref="Boxed"/>) set on a rounded box of its color, like a label.
/// </summary>
internal sealed class TextAnnotation : Annotation, IDisposable
{
    public const string FontFamily = "Segoe UI Variable Display";
    public static readonly Windows.UI.Text.FontWeight FontWeight = FontWeights.SemiBold;

    /// <summary>Line height as a multiple of font size for this font, used to size text from a dragged box.</summary>
    public const float LineHeightRatio = 1.33f;

    private static readonly CanvasStrokeStyle RoundJoin = new() { LineJoin = CanvasLineJoin.Round };

    private CanvasTextLayout? _layout;
    private (string Text, float Size, float? Wrap) _layoutKey;

    public TextAnnotation(Vector2 position, Color color, int weight, float unit)
        : base(color, weight, unit)
    {
        Position = position;
    }

    /// <summary>Top-left of the first line.</summary>
    public Vector2 Position { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Set the text on a box of <see cref="Annotation.Color"/>, with contrasting text.</summary>
    public bool Boxed { get; set; }

    /// <summary>A size picked by dragging a text box. Cleared when the size slider is used.</summary>
    public float? FontSizeOverride { get; set; }

    /// <summary>Width to wrap lines at, from a dragged text box; null means a single line per paragraph.</summary>
    public float? WrapWidth { get; set; }

    public float FontSize => FontSizeOverride ?? (10 + Weight * 4) * Unit;

    private float OutlineWidth => FontSize * 0.14f;

    private Vector2 BoxPadding => new(FontSize * 0.35f, FontSize * 0.15f);

    /// <summary>The ink color of the letters themselves.</summary>
    public Color TextColor => Boxed ? Color.Contrasting() : Color;

    private CanvasTextLayout Layout
    {
        get
        {
            var key = (Text, FontSize, WrapWidth);
            if (_layout is null || _layoutKey != key)
            {
                _layout?.Dispose();
                var format = new CanvasTextFormat
                {
                    FontFamily = FontFamily,
                    FontSize = FontSize,
                    FontWeight = FontWeight,
                    WordWrapping = WrapWidth is null ? CanvasWordWrapping.NoWrap : CanvasWordWrapping.Wrap,
                };
                _layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), Text, format, WrapWidth ?? 0, 0);
                _layoutKey = key;
            }
            return _layout;
        }
    }

    private Rect TextBounds
    {
        get
        {
            var layout = Layout.LayoutBounds;
            return new Rect(Position.X + layout.X, Position.Y + layout.Y, layout.Width, layout.Height);
        }
    }

    private Rect BoxBounds
    {
        get
        {
            var text = TextBounds;
            var pad = BoxPadding;
            return new Rect(text.X - pad.X, text.Y - pad.Y, text.Width + pad.X * 2, text.Height + pad.Y * 2);
        }
    }

    public override Rect Bounds => Boxed ? BoxBounds : TextBounds.Inflate(OutlineWidth);

    public override void Draw(CanvasDrawingSession ds) => Draw(ds, chromeOnly: false);

    /// <param name="chromeOnly">
    /// Draws just the outline or box, for use behind the live text box while typing, so
    /// editing looks the same as the finished text.
    /// </param>
    public void Draw(CanvasDrawingSession ds, bool chromeOnly)
    {
        if (Boxed)
        {
            float radius = FontSize * 0.25f;
            ds.FillRoundedRectangle(BoxBounds, radius, radius, Color);
            if (!chromeOnly && Text.Length > 0)
                ds.DrawTextLayout(Layout, Position, TextColor);
            return;
        }

        if (string.IsNullOrEmpty(Text))
            return;

        using var geometry = CanvasGeometry.CreateText(Layout);
        ds.DrawGeometry(geometry, Position, Color.Contrasting(), OutlineWidth * 2, RoundJoin);
        if (!chromeOnly)
            ds.FillGeometry(geometry, Position, Color);
    }

    public override bool HitTest(Vector2 point, float tolerance) =>
        Bounds.Inflate(tolerance).Contains(point.ToPoint());

    public override void MoveBy(Vector2 delta) => Position += delta;

    protected override void OnCloned()
    {
        // The layout is rebuilt lazily; sharing it would let one copy dispose the other's.
        _layout = null;
    }

    public void Dispose() => _layout?.Dispose();
}
