using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Windshot.Editing;

/// <summary>
/// The shape tool: rectangles, ellipses and triangles dragged out as boxes, polygons clicked
/// out corner by corner, and filling them in.
/// </summary>
public sealed partial class EditorWindow
{
    private ShapeKind _shapeKind;
    private bool _fillShapes;
    /// <summary>The sticker the tool places, by icon name.</summary>
    private string _sticker = Stickers.All[0].Icon;

    /// <summary>A polygon being clicked out; it's a layer already, finished by <see cref="FinishPolygon"/>.</summary>
    private PolygonAnnotation? _polygonDraft;
    /// <summary>Where the next corner would go: the end of the line from the last one.</summary>
    private Vector2 _polygonCursor;

    private AppBarToggleButton[] ShapeButtons => [ShapeRectangleButton, ShapeEllipseButton, ShapeTriangleButton, ShapePolygonButton, ShapeStickerButton];

    private static string ShapeIcon(ShapeKind kind) => kind switch
    {
        ShapeKind.Ellipse => "oval",
        ShapeKind.Triangle => "triangle",
        ShapeKind.Polygon => "pentagon",
        ShapeKind.Sticker => "sticker",
        _ => "rectangle_landscape",
    };

    private void InitializeShapes()
    {
        foreach (var button in ShapeButtons.Where(b => b != ShapeStickerButton))
            BindIcon(button, ShapeIcon(Enum.Parse<ShapeKind>((string)button.Tag)));
        BindIcon(FillButton, "paint_bucket");
        foreach (var (icon, name) in Stickers.All)
        {
            var button = new Button
            {
                Width = 44,
                Height = 44,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = Shell.FluentIcons.Create(icon, filled: false),
            };
            ToolTipService.SetToolTip(button, name);
            button.Click += (_, _) =>
            {
                StickerFlyout.Hide();
                SetSticker(icon);
            };
            StickerGrid.Children.Add(button);
        }
    }

    private void ShapeButton_Click(object sender, RoutedEventArgs e)
    {
        var kind = Enum.Parse<ShapeKind>((string)((FrameworkElement)sender).Tag);
        SetShapeKind(kind);
        // The sticker button also opens the stickers to pick from.
        if (kind == ShapeKind.Sticker)
            Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(ShapeStickerButton);
    }

    private void SetSticker(string icon)
    {
        _sticker = icon;
        if (_selected is ShapeAnnotation { Kind: ShapeKind.Sticker } sticker && sticker.StickerIcon != icon)
        {
            sticker.StickerIcon = icon;
            Commit();
        }
        SetShapeKind(ShapeKind.Sticker);
    }

    /// <summary>A sticker's side when placed with a click, from the size setting, in image pixels.</summary>
    private float StickerSide(int weight) => (24 + weight * 6) * Unit;

    /// <summary>The shape the tool draws next; a selected rectangle, ellipse or triangle becomes it too.</summary>
    private void SetShapeKind(ShapeKind kind)
    {
        FinishPolygon();
        _shapeKind = kind;
        if (_selected is ShapeAnnotation shape && kind != ShapeKind.Polygon &&
            (shape.Kind != kind || (kind == ShapeKind.Sticker && shape.StickerIcon != _sticker)))
        {
            shape.Kind = kind;
            shape.StickerIcon = _sticker;
            Commit();
        }
        SyncShapes();
        Canvas.Invalidate();
    }

    private void FillButton_Click(object sender, RoutedEventArgs e) => SetFill(FillButton.IsChecked == true);

    private void SetFill(bool fill)
    {
        _fillShapes = fill;
        if (_selected is IFillable shape && shape.Filled != fill)
        {
            shape.Filled = fill;
            Commit();
        }
        SyncShapes();
        Canvas.Invalidate();
    }

    /// <summary>The shape buttons and Fill toggle (on the options bar for shapes).</summary>
    private void SyncShapes()
    {
        var kind = _selected switch
        {
            ShapeAnnotation shape => shape.Kind,
            PolygonAnnotation => ShapeKind.Polygon,
            _ => _shapeKind,
        };
        foreach (var button in ShapeButtons)
            button.IsChecked = (string)button.Tag == kind.ToString();
        // The sticker button shows the sticker it places, or the selected one.
        string sticker = _selected is ShapeAnnotation { Kind: ShapeKind.Sticker } selected ? selected.StickerIcon : _sticker;
        ShapeStickerButton.Icon = Shell.FluentIcons.Create(sticker, filled: kind == ShapeKind.Sticker);
        FillButton.IsChecked = (_selected as IFillable)?.Filled ?? _fillShapes;
    }

    // ---- Polygons ----------------------------------------------------------------------

    private void StartPolygon(Vector2 p)
    {
        _polygonDraft = new PolygonAnnotation(p, _color, _toolWeights[Tool.Rectangle], Unit) { Filled = _fillShapes };
        _polygonCursor = p;
        AddLayer(_polygonDraft);
        RefreshLayers();
        Canvas.Invalidate();
    }

    /// <summary>A click while clicking out a polygon: another corner, or on the first corner, done.</summary>
    private void PolygonPressed(Vector2 p)
    {
        var draft = _polygonDraft!;
        float reach = (HandleRadius + HitTolerance) / _view.M11;
        if (draft.Points.Count >= 3 && Vector2.Distance(p, draft.Points[0]) <= reach)
        {
            FinishPolygon();
            return;
        }
        // The second click of a double-click lands on the last corner; that finishes instead.
        if (Vector2.Distance(p, draft.Points[^1]) > reach / 2)
            draft.Points.Add(p);
        Canvas.Invalidate();
    }

    /// <summary>
    /// Closes the polygon being clicked out and selects it. One with fewer than three
    /// corners isn't a polygon, so it's dropped.
    /// </summary>
    private void FinishPolygon()
    {
        if (_polygonDraft is not { } draft)
            return;
        _polygonDraft = null;
        if (draft.Points.Count < 3)
        {
            _document.Annotations.Remove(draft);
            RefreshLayers();
        }
        else
        {
            draft.Closed = true;
            Commit();
            Select(draft);
        }
        Canvas.Invalidate();
    }

    /// <summary>Undo while clicking out a polygon takes back the last corner.</summary>
    private void UndoPolygonCorner()
    {
        var draft = _polygonDraft!;
        if (draft.Points.Count > 1)
        {
            draft.Points.RemoveAt(draft.Points.Count - 1);
        }
        else
        {
            _polygonDraft = null;
            _document.Annotations.Remove(draft);
            RefreshLayers();
        }
        Canvas.Invalidate();
    }

    /// <summary>The line to where the next corner would go, and a ring on the first corner once clicking it would finish.</summary>
    private void DrawPolygonDraft(CanvasDrawingSession ds)
    {
        if (_polygonDraft is not { } draft)
            return;
        float px = 1 / _view.M11;
        using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        ds.DrawLine(draft.Points[^1], _polygonCursor, AccentColor, 1.5f * px, dashed);
        if (draft.Points.Count >= 3)
        {
            ds.FillCircle(draft.Points[0], HandleRadius * px, Microsoft.UI.Colors.White);
            ds.DrawCircle(draft.Points[0], HandleRadius * px, AccentColor, 1.5f * px);
        }
    }
}
