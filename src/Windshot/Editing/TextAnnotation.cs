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
    public const string DefaultFontFamily = "Segoe UI Variable Display";
    public static readonly Windows.UI.Text.FontWeight FontWeight = FontWeights.SemiBold;

    /// <summary>
    /// Fonts that come with Windows 11, so text looks the same on any PC: (name shown, family).
    /// </summary>
    public static readonly (string Name, string Family)[] Fonts =
    [
        ("Segoe UI", DefaultFontFamily),
        ("Bahnschrift", "Bahnschrift"),
        ("Georgia", "Georgia"),
        ("Consolas", "Consolas"),
        ("Courier New", "Courier New"),
        ("Segoe Print", "Segoe Print"),
        ("Segoe Script", "Segoe Script"),
        ("Ink Free", "Ink Free"),
        ("Comic Sans MS", "Comic Sans MS"),
        ("Impact", "Impact"),
    ];

    /// <summary>The font, by family name; any installed font works, e.g. one typed into settings.json.</summary>
    public string FontFamily { get; set; } = DefaultFontFamily;

    /// <summary>Line height as a multiple of font size for this font, used to size text from a dragged box.</summary>
    public const float LineHeightRatio = 1.33f;

    private static readonly CanvasStrokeStyle RoundJoin = new() { LineJoin = CanvasLineJoin.Round };

    private CanvasTextLayout? _layout;
    private (string Text, string Font, float Size, float? Wrap) _layoutKey;

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
            var key = (Text, FontFamily, FontSize, WrapWidth);
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

    public override Rect Frame => Boxed ? BoxBounds : TextBounds.Inflate(OutlineWidth);

    protected override void DrawUnrotated(CanvasDrawingSession ds) => Draw(ds, chromeOnly: false);

    /// <summary>
    /// Just the outline or box, for use behind the live text box while typing, so editing
    /// looks the same as the finished text.
    /// </summary>
    public void DrawChrome(CanvasDrawingSession ds) => DrawRotated(ds, s => Draw(s, chromeOnly: true));

    private void Draw(CanvasDrawingSession ds, bool chromeOnly)
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

    protected override bool HitTestUnrotated(Vector2 point, float tolerance) =>
        Frame.Inflate(tolerance).Contains(point.ToPoint());

    protected override void Offset(Vector2 delta) => Position += delta;

    /// <summary>The smallest and largest font sizes reachable by dragging a corner, in DIPs.</summary>
    private const float MinResizeSize = 8, MaxResizeSize = 400;

    /// <summary>The four corners, clockwise from top-left: dragging one scales the text.</summary>
    protected override IReadOnlyList<Vector2> UnrotatedHandles
    {
        get
        {
            if (string.IsNullOrEmpty(Text))
                return [];
            var b = Frame;
            return [new((float)b.Left, (float)b.Top), new((float)b.Right, (float)b.Top),
                    new((float)b.Right, (float)b.Bottom), new((float)b.Left, (float)b.Bottom)];
        }
    }

    /// <summary>
    /// Scales the text (font size, and the wrapping width if there is one) so the dragged
    /// corner follows the pointer while the opposite corner stays put.
    /// </summary>
    protected override void MoveUnrotatedHandle(int index, Vector2 position)
    {
        var corners = UnrotatedHandles;
        if (corners.Count < 4)
            return;
        var anchor = corners[(index + 2) % 4];
        var diagonal = corners[index] - anchor;
        if (diagonal.LengthSquared() < 1)
            return;

        // How far along the diagonal the pointer is: 1 keeps the size, 2 doubles it.
        float scale = Vector2.Dot(position - anchor, diagonal) / diagonal.LengthSquared();
        float size = Math.Clamp(FontSize * scale, MinResizeSize * Unit, MaxResizeSize * Unit);
        scale = size / FontSize;
        FontSizeOverride = size;
        if (WrapWidth is float wrap)
            WrapWidth = wrap * scale;

        // Shift so the opposite corner is back where it was.
        Position += anchor - UnrotatedHandles[(index + 2) % 4];
    }

    protected override void OnCloned()
    {
        // The layout is rebuilt lazily; sharing it would let one copy dispose the other's.
        _layout = null;
    }

    public void Dispose() => _layout?.Dispose();
}
