using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Winshot.Capture;

namespace Winshot.Editing;

internal enum Tool
{
    Select,
    Arrow,
    Rectangle,
    Text,
}

public sealed partial class EditorWindow : Window
{
    private const float ViewPadding = 32;
    private const float HitTolerance = 6;
    private const float HandleRadius = 5;
    private const float MinShapeSize = 3;
    private static readonly Color DefaultColor = Color.FromArgb(255, 255, 59, 48);
    private static readonly Color AccentColor = Color.FromArgb(255, 0, 120, 212);

    private readonly Document _document;
    private readonly Dictionary<Tool, int> _toolWeights = new()
    {
        [Tool.Arrow] = 4,
        [Tool.Rectangle] = 3,
        [Tool.Text] = 4,
    };

    private Tool _tool;
    private Annotation? _selected;
    private CanvasImageBrush? _checkerBrush;
    private bool _syncingSlider;

    // Maps document (image pixel) space to control DIPs. Frozen while dragging or typing so
    // the canvas doesn't shift under the cursor as it expands; it re-fits afterwards.
    private Matrix3x2 _view = Matrix3x2.Identity;
    private DragState? _drag;
    private TextAnnotation? _editingText;
    private TextBox? _textBox;

    /// <param name="Handle">Index into <see cref="Annotation.Handles"/>, or -1 to move the whole object.</param>
    private sealed record DragState(uint PointerId, Annotation Target, int Handle, bool Creating)
    {
        public Vector2 Last { get; set; }
    }

    internal EditorWindow(CapturedImage capture)
    {
        InitializeComponent();
        _document = new Document(capture);

        // Hooked up here rather than in XAML: setting Minimum during load fires ValueChanged
        // before the rest of the window exists.
        WeightSlider.ValueChanged += WeightSlider_ValueChanged;
        SizeToCapture(capture);
        SetTool(Tool.Arrow);
        Closed += (_, _) => _document.Dispose();
    }

    private float Unit => (float)_document.SourceScale;

    private void SizeToCapture(CapturedImage capture)
    {
        // Aim to show the capture at 1:1 physical pixels, capped to most of the work area.
        var workArea = DisplayArea.GetFromPoint(new PointInt32(0, 0), DisplayAreaFallback.Primary).WorkArea;
        int chromeWidth = (int)(2 * ViewPadding * capture.Scale + 120);
        int chromeHeight = (int)((2 * ViewPadding + 90) * capture.Scale);
        int width = Math.Clamp(capture.Width + chromeWidth, (int)(720 * capture.Scale), (int)(workArea.Width * 0.85));
        int height = Math.Clamp(capture.Height + chromeHeight, (int)(360 * capture.Scale), (int)(workArea.Height * 0.85));
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2));
    }

    // ---- Tools and size ----------------------------------------------------------------

    private void SetTool(Tool tool)
    {
        CommitTextEdit();
        _tool = tool;
        SelectButton.IsChecked = tool == Tool.Select;
        ArrowButton.IsChecked = tool == Tool.Arrow;
        RectangleButton.IsChecked = tool == Tool.Rectangle;
        TextButton.IsChecked = tool == Tool.Text;

        if (tool != Tool.Select)
            Select(null);
        else
            SyncSlider();
    }

    private void ToolButton_Click(object sender, RoutedEventArgs e) =>
        SetTool(Enum.Parse<Tool>((string)((FrameworkElement)sender).Tag));

    private static Tool ToolFor(Annotation annotation) => annotation switch
    {
        ArrowAnnotation => Tool.Arrow,
        TextAnnotation => Tool.Text,
        _ => Tool.Rectangle,
    };

    private void Select(Annotation? annotation)
    {
        _selected = annotation;
        SyncSlider();
        Canvas.Invalidate();
    }

    /// <summary>The slider shows the selected object's size, or the size the current tool will draw at.</summary>
    private void SyncSlider()
    {
        int? weight = _selected?.Weight ?? (_toolWeights.TryGetValue(_tool, out int w) ? w : null);
        _syncingSlider = true;
        WeightSlider.IsEnabled = weight is not null;
        if (weight is int value)
            WeightSlider.Value = value;
        _syncingSlider = false;
    }

    private void WeightSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSlider)
            return;
        SetWeight((int)e.NewValue);
    }

    private void SetWeight(int weight)
    {
        weight = Math.Clamp(weight, Annotation.MinWeight, Annotation.MaxWeight);
        if (_selected is not null)
        {
            _selected.Weight = weight;
            _toolWeights[ToolFor(_selected)] = weight; // the next one drawn matches
            if (_selected == _editingText && _textBox is not null)
                _textBox.FontSize = _editingText.FontSize * _view.M11;
        }
        else if (_toolWeights.ContainsKey(_tool))
        {
            _toolWeights[_tool] = weight;
        }
        SyncSlider();
        Canvas.Invalidate();
    }

    // ---- Keyboard ----------------------------------------------------------------------

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // While typing, every key belongs to the text box.
        if (_editingText is not null || e.OriginalSource is TextBox)
            return;

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool handled = true;
        switch (e.Key)
        {
            case VirtualKey.C when ctrl: Copy_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.S when ctrl: Save_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.V when !ctrl: SetTool(Tool.Select); break;
            case VirtualKey.A when !ctrl: SetTool(Tool.Arrow); break;
            case VirtualKey.R when !ctrl: SetTool(Tool.Rectangle); break;
            case VirtualKey.T when !ctrl: SetTool(Tool.Text); break;
            case (VirtualKey)219: SetWeight((int)WeightSlider.Value - 1); break; // [
            case (VirtualKey)221: SetWeight((int)WeightSlider.Value + 1); break; // ]
            case VirtualKey.Delete or VirtualKey.Back when _selected is not null:
                _document.Annotations.Remove(_selected);
                (_selected as IDisposable)?.Dispose();
                Select(null);
                break;
            case VirtualKey.Escape when _selected is not null: Select(null); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    // ---- Rendering ---------------------------------------------------------------------

    private void Canvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // Small checkerboard tile, wrapped, marks transparent canvas area.
        const float cell = 8;
        var tile = new CanvasRenderTarget(sender, cell * 2, cell * 2, 96);
        using (var ds = tile.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(255, 255, 255, 255));
            var shade = Color.FromArgb(255, 230, 230, 230);
            ds.FillRectangle(0, 0, cell, cell, shade);
            ds.FillRectangle(cell, cell, cell, cell, shade);
        }
        _checkerBrush = new CanvasImageBrush(sender, tile)
        {
            ExtendX = CanvasEdgeBehavior.Wrap,
            ExtendY = CanvasEdgeBehavior.Wrap,
        };
    }

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_drag is null && _editingText is null)
            _view = FitView(sender);

        var ds = args.DrawingSession;
        ds.Transform = _view;

        if (_checkerBrush is not null)
            ds.FillRectangle(_document.Bounds, _checkerBrush);

        _document.Render(ds, skip: _editingText);
        _editingText?.Draw(ds, outlineOnly: true); // the text box draws the fill on top

        if (_selected is not null && _selected != _editingText)
            DrawSelection(ds, _selected);
    }

    private void DrawSelection(CanvasDrawingSession ds, Annotation annotation)
    {
        float px = 1 / _view.M11;
        if (annotation.Handles.Count == 0)
        {
            using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            ds.DrawRectangle(annotation.Bounds.Inflate(2 * px), AccentColor, 1.5f * px, dashed);
            return;
        }

        foreach (var handle in annotation.Handles)
        {
            ds.FillCircle(handle, HandleRadius * px, Color.FromArgb(255, 255, 255, 255));
            ds.DrawCircle(handle, HandleRadius * px, AccentColor, 1.5f * px);
        }
    }

    private Matrix3x2 FitView(CanvasControl canvas)
    {
        var bounds = _document.Bounds;
        var available = canvas.ActualSize - new Vector2(ViewPadding * 2);
        float rasterScale = (float)(canvas.XamlRoot?.RasterizationScale ?? 1.0);

        // Never upscale past 1 image pixel per device pixel; shrink to fit if needed.
        float scale = Math.Min(1 / rasterScale,
            Math.Min(available.X / (float)bounds.Width, available.Y / (float)bounds.Height));
        scale = Math.Max(scale, 0.01f);

        var size = new Vector2((float)bounds.Width, (float)bounds.Height) * scale;
        var offset = (canvas.ActualSize - size) / 2;
        return Matrix3x2.CreateTranslation(-(float)bounds.X, -(float)bounds.Y)
             * Matrix3x2.CreateScale(scale)
             * Matrix3x2.CreateTranslation(offset);
    }

    private Vector2 ToDocument(Vector2 controlPoint)
    {
        Matrix3x2.Invert(_view, out var inverse);
        return Vector2.Transform(controlPoint, inverse);
    }

    // ---- Pointer interaction -----------------------------------------------------------

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Canvas);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;

        // Clicking away from the text box just finishes typing.
        if (_editingText is not null)
        {
            CommitTextEdit();
            return;
        }

        var p = ToDocument(point.Position.ToVector2());
        float tolerance = HitTolerance / _view.M11;

        if (_selected is not null && HitHandle(_selected, p) is int handle)
        {
            StartDrag(e, _selected, handle, creating: false, p);
        }
        else if (_document.HitTest(p, tolerance) is Annotation hit)
        {
            // Clicking an existing object grabs it, whichever tool is active.
            Select(hit);
            if (hit is TextAnnotation text && _tool == Tool.Text)
                BeginTextEdit(text);
            else
                StartDrag(e, hit, handle: -1, creating: false, p);
        }
        else
        {
            CreateAt(e, p);
        }
    }

    private void CreateAt(PointerRoutedEventArgs e, Vector2 p)
    {
        if (_tool == Tool.Select)
        {
            Select(null);
            return;
        }

        int weight = _toolWeights[_tool];
        if (_tool == Tool.Text)
        {
            var text = new TextAnnotation(p, DefaultColor, weight, Unit);
            // Put the click at the middle of the first line, where the caret appears.
            text.Position -= new Vector2(0, text.FontSize * 0.65f);
            _document.Annotations.Add(text);
            Select(text);
            BeginTextEdit(text);
            return;
        }

        TwoPointAnnotation shape = _tool == Tool.Arrow
            ? new ArrowAnnotation(p, DefaultColor, weight, Unit)
            : new RectangleAnnotation(p, DefaultColor, weight, Unit);
        _document.Annotations.Add(shape);
        Select(shape);
        StartDrag(e, shape, TwoPointAnnotation.EndHandle, creating: true, p);
    }

    private int? HitHandle(Annotation annotation, Vector2 p)
    {
        float reach = (HandleRadius + HitTolerance) / _view.M11;
        var handles = annotation.Handles;
        for (int i = 0; i < handles.Count; i++)
        {
            if (Vector2.Distance(handles[i], p) <= reach)
                return i;
        }
        return null;
    }

    private void StartDrag(PointerRoutedEventArgs e, Annotation target, int handle, bool creating, Vector2 p)
    {
        _drag = new DragState(e.Pointer.PointerId, target, handle, creating) { Last = p };
        CanvasHost.CapturePointer(e.Pointer);
        Canvas.Invalidate();
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null || e.Pointer.PointerId != _drag.PointerId)
            return;

        var p = ToDocument(e.GetCurrentPoint(Canvas).Position.ToVector2());
        if (_drag.Handle >= 0)
            _drag.Target.MoveHandle(_drag.Handle, p);
        else
            _drag.Target.MoveBy(p - _drag.Last);
        _drag.Last = p;

        Canvas.Invalidate();
        e.Handled = true;
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null || e.Pointer.PointerId != _drag.PointerId)
            return;

        // A click without a drag shouldn't leave an invisible zero-size shape behind.
        if (_drag.Creating && _drag.Target is TwoPointAnnotation shape &&
            Math.Abs(shape.End.X - shape.Start.X) < MinShapeSize && Math.Abs(shape.End.Y - shape.Start.Y) < MinShapeSize)
        {
            _document.Annotations.Remove(shape);
            Select(null);
        }

        _drag = null;
        CanvasHost.ReleasePointerCapture(e.Pointer);
        Canvas.Invalidate();
        e.Handled = true;
    }

    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var p = ToDocument(e.GetPosition(Canvas).ToVector2());
        if (_document.HitTest(p, HitTolerance / _view.M11) is TextAnnotation text)
        {
            Select(text);
            BeginTextEdit(text);
            e.Handled = true;
        }
    }

    // ---- Inline text editing -----------------------------------------------------------

    private void BeginTextEdit(TextAnnotation text)
    {
        CommitTextEdit();
        _editingText = text;

        float scale = _view.M11;
        var color = new SolidColorBrush(text.Color);
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var box = new TextBox
        {
            Text = text.Text,
            AcceptsReturn = true,
            FontFamily = new FontFamily(TextAnnotation.FontFamily),
            FontWeight = TextAnnotation.FontWeight,
            FontSize = text.FontSize * scale,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            MinWidth = 24,
            MinHeight = 0,
        };
        // Strip the default chrome in every visual state so it reads as text on the canvas.
        foreach (string state in new[] { "", "PointerOver", "Focused", "Disabled" })
        {
            box.Resources[$"TextControlBackground{state}"] = clear;
            box.Resources[$"TextControlBorderBrush{state}"] = clear;
            box.Resources[$"TextControlForeground{state}"] = color;
        }

        var position = Vector2.Transform(text.Position, _view);
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(box, position.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(box, position.Y);

        box.TextChanged += (_, _) =>
        {
            text.Text = box.Text;
            Canvas.Invalidate();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                CommitTextEdit();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => CommitTextEdit();
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };

        _textBox = box;
        TextLayer.Children.Add(box);
        Canvas.Invalidate();
    }

    private void CommitTextEdit()
    {
        if (_editingText is not { } text)
            return;

        // Clear state first: removing the focused box raises LostFocus, which calls back in here.
        _editingText = null;
        if (_textBox is not null)
        {
            TextLayer.Children.Remove(_textBox);
            _textBox = null;
        }

        if (string.IsNullOrWhiteSpace(text.Text))
        {
            _document.Annotations.Remove(text);
            text.Dispose();
            if (_selected == text)
                Select(null);
        }
        Canvas.Invalidate();
    }

    // ---- Export ------------------------------------------------------------------------

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        var png = await _document.EncodePngAsync();
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(png));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = $"Winshot {DateTime.Now:yyyy-MM-dd HHmmss}",
        };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        using var png = await _document.EncodePngAsync();
        using var output = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        output.Size = 0;
        await RandomAccessStream.CopyAndCloseAsync(png.GetInputStreamAt(0), output.GetOutputStreamAt(0));
    }
}
