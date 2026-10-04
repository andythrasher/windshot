using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Winshot.Capture;

namespace Winshot.Editing;

internal enum Tool
{
    Select,
    Rectangle,
}

public sealed partial class EditorWindow : Window
{
    private const float ViewPadding = 32;
    private const float HitTolerance = 6;
    private static readonly Color DefaultColor = Color.FromArgb(255, 255, 59, 48);

    private readonly Document _document;
    private Tool _tool = Tool.Rectangle;
    private Annotation? _selected;
    private CanvasImageBrush? _checkerBrush;

    // Maps document (image pixel) space to control DIPs. Frozen while dragging so the
    // canvas doesn't shift under the cursor as it expands; it re-fits on release.
    private Matrix3x2 _view = Matrix3x2.Identity;
    private DragState? _drag;

    private sealed record DragState(uint PointerId, Annotation Target, bool Creating)
    {
        public Vector2 Last { get; set; }
    }

    internal EditorWindow(CapturedImage capture)
    {
        InitializeComponent();
        _document = new Document(capture);

        SizeToCapture(capture);
        SetTool(Tool.Rectangle);
        Closed += (_, _) => _document.Dispose();
    }

    private void SizeToCapture(CapturedImage capture)
    {
        // Aim to show the capture at 1:1 physical pixels, capped to most of the work area.
        var workArea = DisplayArea.GetFromPoint(new PointInt32(0, 0), DisplayAreaFallback.Primary).WorkArea;
        int chromeWidth = (int)(2 * ViewPadding * capture.Scale + 120);
        int chromeHeight = (int)((2 * ViewPadding + 90) * capture.Scale);
        int width = Math.Clamp(capture.Width + chromeWidth, (int)(560 * capture.Scale), (int)(workArea.Width * 0.85));
        int height = Math.Clamp(capture.Height + chromeHeight, (int)(360 * capture.Scale), (int)(workArea.Height * 0.85));
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2));
    }

    // ---- Tools -------------------------------------------------------------------------

    private void SetTool(Tool tool)
    {
        _tool = tool;
        SelectButton.IsChecked = tool == Tool.Select;
        RectangleButton.IsChecked = tool == Tool.Rectangle;
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e) => SetTool(Tool.Select);
    private void RectangleButton_Click(object sender, RoutedEventArgs e) => SetTool(Tool.Rectangle);

    private void SelectTool_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetTool(Tool.Select);
        args.Handled = true;
    }

    private void RectangleTool_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetTool(Tool.Rectangle);
        args.Handled = true;
    }

    private void Delete_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_selected is null)
            return;
        _document.Annotations.Remove(_selected);
        _selected = null;
        Canvas.Invalidate();
        args.Handled = true;
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
        if (_drag is null)
            _view = FitView(sender);

        var ds = args.DrawingSession;
        ds.Transform = _view;

        var bounds = _document.Bounds;
        if (_checkerBrush is not null)
            ds.FillRectangle(bounds, _checkerBrush);

        _document.Render(ds);

        if (_selected is not null)
        {
            float px = 1 / _view.M11;
            using var style = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            ds.DrawRectangle(_selected.Bounds.Inflate(4 * px), Color.FromArgb(255, 0, 120, 212), 1.5f * px, style);
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
        var point = e.GetCurrentPoint(Canvas);        if (!point.Properties.IsLeftButtonPressed)
            return;

        var p = ToDocument(point.Position.ToVector2());
        float tolerance = HitTolerance / _view.M11;

        // Clicking an existing object always grabs it, whichever tool is active.
        var hit = _document.HitTest(p, tolerance);
        if (hit is not null)
        {
            _selected = hit;
            _drag = new DragState(point.PointerId, hit, Creating: false) { Last = p };
        }
        else if (_tool == Tool.Rectangle)
        {
            var rect = new RectangleAnnotation(p, DefaultColor, thickness: 4);
            _document.Annotations.Add(rect);
            _selected = rect;
            _drag = new DragState(point.PointerId, rect, Creating: true) { Last = p };
        }
        else
        {
            _selected = null;
        }

        if (_drag is not null)
            CanvasHost.CapturePointer(e.Pointer);
        Canvas.Invalidate();
        e.Handled = true;
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null || e.Pointer.PointerId != _drag.PointerId)
            return;

        var p = ToDocument(e.GetCurrentPoint(Canvas).Position.ToVector2());
        if (_drag.Creating && _drag.Target is RectangleAnnotation rect)
            rect.End = p;
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
        // A click without a drag shouldn't leave an invisible zero-size rectangle behind.
        if (_drag.Creating && _drag.Target is RectangleAnnotation rect &&
            Math.Abs(rect.End.X - rect.Start.X) < 3 && Math.Abs(rect.End.Y - rect.Start.Y) < 3)
        {
            _document.Annotations.Remove(rect);
            _selected = null;
        }

        _drag = null;
        CanvasHost.ReleasePointerCapture(e.Pointer);
        Canvas.Invalidate();
        e.Handled = true;
    }

    // ---- Export ------------------------------------------------------------------------

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var png = await _document.EncodePngAsync();
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(png));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
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
