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
using Windshot.Capture;

namespace Windshot.Editing;

internal enum Tool
{
    Select,
    Arrow,
    Rectangle,
    Text,
    Blur,
    Step,
    Spotlight,
    Highlighter,
}

public sealed partial class EditorWindow : Window
{
    private const float ViewPadding = 32;
    private const float HitTolerance = 6;
    private const float HandleRadius = 5;
    private const float MinShapeSize = 3;
    private static readonly Color AccentColor = Color.FromArgb(255, 0, 120, 212);

    private readonly Document _document;
    private readonly System.Drawing.Rectangle _desktopBounds;
    private readonly List<Microsoft.UI.Xaml.Controls.Button> _backdropSwatches = new();
    private readonly History _history;
    private readonly List<Microsoft.UI.Xaml.Controls.Button> _swatches = new();

    // Preferences: loaded from settings when the editor opens, saved back when it closes.
    private readonly Dictionary<Tool, int> _toolWeights;
    private Color _color;
    // The highlighter keeps its own color: yellow highlights shouldn't turn your arrows yellow.
    private Color _highlightColor;
    private bool _pixelate;
    private bool _textBoxed;
    private bool _beautify;
    private int _backdropPreset;
    private double _backdropPadding;

    private Tool _tool;
    private Annotation? _selected;
    private CanvasImageBrush? _checkerBrush;
    private bool _syncingSlider;

    // Maps document (image pixel) space to control DIPs. Frozen while dragging or typing so
    // the canvas doesn't shift under the cursor as it expands; it re-fits afterwards.
    private Matrix3x2 _view = Matrix3x2.Identity;
    private DragState? _drag;
    private TextDrag? _textDrag;
    private TextAnnotation? _editingText;
    private TextBox? _textBox;
    private bool _editingIsNew;
    private string _textBeforeEdit = "";

    /// <param name="Handle">Index into <see cref="Annotation.Handles"/>, or -1 to move the whole object.</param>
    private sealed record DragState(uint PointerId, Annotation Target, int Handle, bool Creating)
    {
        public Vector2 Last { get; set; }

        /// <summary>Whether anything changed; a click that only selects shouldn't add an undo step.</summary>
        public bool Moved { get; set; }
    }

    /// <summary>A text box being dragged out with the text tool (document coordinates).</summary>
    private sealed record TextDrag(uint PointerId, Vector2 Start)
    {
        public Vector2 End { get; set; }
    }

    internal EditorWindow(CapturedImage capture)
    {
        InitializeComponent();
        InitializeIcons();
        _document = new Document(capture);
        _desktopBounds = capture.DesktopBounds;
        _startAtTop = capture.IsScrolling;
        _history = new History(_document);

        var prefs = Settings.Current;
        _toolWeights = new Dictionary<Tool, int>(prefs.Editor.Sizes);
        _color = ColorExtensions.TryParseHex(prefs.Editor.Color, out var color) ? color : Palette.Colors[0].Color;
        _highlightColor = ColorExtensions.TryParseHex(prefs.Editor.HighlighterColor, out var highlight) ? highlight : Palette.Colors[2].Color;
        _pixelate = prefs.Editor.Pixelate;
        _textBoxed = prefs.Editor.TextBackground;
        _beautify = prefs.Beautify.OnByDefault;
        _backdropPreset = Math.Max(0, Array.FindIndex(BackdropPresets.All,
            p => p.Name.Equals(prefs.Beautify.Preset, StringComparison.OrdinalIgnoreCase)));
        _backdropPadding = Math.Clamp(prefs.Beautify.Padding, 16, 160);

        // Hooked up here rather than in XAML: setting Minimum during load fires ValueChanged
        // before the rest of the window exists.
        WeightSlider.ValueChanged += WeightSlider_ValueChanged;
        BuildSwatches();
        BuildBackdropControls();
        SizeToCapture(capture);
        if (File.Exists(AppIcon.FilePath))
            AppWindow.SetIcon(AppIcon.FilePath);
        // Start in Select so a stray click doesn't draw something.
        SetTool(Tool.Select);
        Closed += (_, _) =>
        {
            SavePreferences();
            _document.Dispose();
        };
    }

    private float Unit => (float)_document.SourceScale;

    /// <summary>Remembers this editor's sizes, colors and styles for next time.</summary>
    private void SavePreferences()
    {
        var prefs = Settings.Current;
        prefs.Editor.Sizes = new Dictionary<Tool, int>(_toolWeights);
        prefs.Editor.Color = _color.ToHex();
        prefs.Editor.HighlighterColor = _highlightColor.ToHex();
        prefs.Editor.Pixelate = _pixelate;
        prefs.Editor.TextBackground = _textBoxed;
        prefs.Beautify.Preset = BackdropPresets.All[_backdropPreset].Name;
        prefs.Beautify.Padding = _backdropPadding;
        Settings.Save();
    }

    private void SizeToCapture(CapturedImage capture)
    {
        // Aim to show the capture at 1:1 physical pixels, capped to most of the work area.
        var workArea = DisplayArea.GetFromPoint(new PointInt32(0, 0), DisplayAreaFallback.Primary).WorkArea;
        int chromeWidth = (int)(2 * ViewPadding * capture.Scale + 120);
        int chromeHeight = (int)((2 * ViewPadding + 90) * capture.Scale);
        // The minimum keeps the whole toolbar visible.
        int width = Math.Clamp(capture.Width + chromeWidth, (int)(1060 * capture.Scale), (int)(workArea.Width * 0.85));
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
        // Tool buttons are tagged with their Tool name.
        foreach (var button in Toolbar.PrimaryCommands.OfType<AppBarToggleButton>())
        {
            if (button.Tag is string tag)
                button.IsChecked = tag == tool.ToString();
        }

        if (tool != Tool.Select)
        {
            Select(null);
        }
        else
        {
            SyncSlider();
            SyncPixelate();
            SyncTextBackground();
            SyncColor();
        }
        UpdateCursor(null);
    }

    private void ToolButton_Click(object sender, RoutedEventArgs e) =>
        SetTool(Enum.Parse<Tool>((string)((FrameworkElement)sender).Tag));

    private static Tool ToolFor(Annotation annotation) => annotation switch
    {
        ArrowAnnotation => Tool.Arrow,
        TextAnnotation => Tool.Text,
        BlurAnnotation => Tool.Blur,
        StepAnnotation => Tool.Step,
        SpotlightAnnotation => Tool.Spotlight,
        HighlighterAnnotation => Tool.Highlighter,
        _ => Tool.Rectangle,
    };

    private void Select(Annotation? annotation)
    {
        _selected = annotation;
        if (annotation is { IsAreaEffect: false })
            ActiveColor = annotation.Color; // picking up an object's color makes it easy to match
        SyncSlider();
        SyncColor();
        SyncPixelate();
        SyncTextBackground();
        Canvas.Invalidate();
    }

    /// <summary>The text background toggle only appears for the text tool or selected text.</summary>
    private void SyncTextBackground()
    {
        var text = _selected as TextAnnotation;
        TextBackgroundButton.Visibility = text is not null || _tool == Tool.Text ? Visibility.Visible : Visibility.Collapsed;
        TextBackgroundButton.IsChecked = text?.Boxed ?? _textBoxed;
    }

    private void TextBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        _textBoxed = TextBackgroundButton.IsChecked == true;
        if (_selected is TextAnnotation text && text.Boxed != _textBoxed)
        {
            text.Boxed = _textBoxed;
            Commit();
        }
        SyncTextBackground();
        Canvas.Invalidate();
    }

    /// <summary>The Pixelate toggle only appears for the blur tool or a selected blur.</summary>
    private void SyncPixelate()
    {
        var blur = _selected as BlurAnnotation;
        PixelateButton.Visibility = blur is not null || _tool == Tool.Blur ? Visibility.Visible : Visibility.Collapsed;
        PixelateButton.IsChecked = blur?.Pixelate ?? _pixelate;
    }

    private void PixelateButton_Click(object sender, RoutedEventArgs e) => SetPixelate(PixelateButton.IsChecked == true);

    private void SetPixelate(bool pixelate)
    {
        _pixelate = pixelate;
        if (_selected is BlurAnnotation blur && blur.Pixelate != pixelate)
        {
            blur.Pixelate = pixelate;
            Commit();
        }
        SyncPixelate();
        Canvas.Invalidate();
    }

    /// <summary>The slider shows the selected object's size, or the size the current tool will draw at.</summary>
    private void SyncSlider()
    {
        int? weight = _selected switch
        {
            // Text sized by dragging a box shows the nearest slider step.
            TextAnnotation { FontSizeOverride: float size } => Math.Clamp((int)Math.Round((size / Unit - 10) / 4), Annotation.MinWeight, Annotation.MaxWeight),
            not null => _selected.Weight,
            null => _toolWeights.TryGetValue(_tool, out int w) ? w : null,
        };
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
        CommitTextEdit();
        weight = Math.Clamp(weight, Annotation.MinWeight, Annotation.MaxWeight);
        if (_selected is not null)
        {
            _toolWeights[ToolFor(_selected)] = weight; // the next one drawn matches
            // Using the slider on text sized by dragging a box switches it back to slider sizes.
            if (_selected.Weight != weight || _selected is TextAnnotation { FontSizeOverride: not null })
            {
                _selected.Weight = weight;
                if (_selected is TextAnnotation text)
                    text.FontSizeOverride = null;
                Commit(coalesceKey: ("weight", _selected));
            }
        }
        else if (_toolWeights.ContainsKey(_tool))
        {
            _toolWeights[_tool] = weight;
        }
        SyncSlider();
        Canvas.Invalidate();
    }

    // ---- Color -------------------------------------------------------------------------

    private void BuildSwatches()
    {
        for (int i = 0; i < Palette.Colors.Length; i++)
        {
            var (name, color) = Palette.Colors[i];
            var swatch = new Microsoft.UI.Xaml.Controls.Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(color),
                BorderBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            };
            // Keep the swatch's color on hover and press instead of the default button tint.
            swatch.Resources["ButtonBackgroundPointerOver"] = swatch.Background;
            swatch.Resources["ButtonBackgroundPressed"] = swatch.Background;
            ToolTipService.SetToolTip(swatch, $"{name} ({i + 1})");
            swatch.Click += (_, _) =>
            {
                SetColor(color);
                ColorFlyout.Hide();
            };
            _swatches.Add(swatch);
            SwatchPanel.Children.Add(swatch);
        }
        SyncColor();
    }

    private void SetColor(Color color)
    {
        CommitTextEdit();
        ActiveColor = color;
        if (_selected is { IsAreaEffect: false } && _selected.Color != color)
        {
            _selected.Color = color;
            Commit(coalesceKey: ("color", _selected));
        }
        SyncColor();
        Canvas.Invalidate();
    }

    private void SyncColor()
    {
        var color = ActiveColor;
        ColorSwatch.Fill = new SolidColorBrush(color);
        for (int i = 0; i < _swatches.Count; i++)
            _swatches[i].BorderThickness = new Thickness(Palette.Colors[i].Color == color ? 3 : 1);
    }

    /// <summary>
    /// The color the palette shows and edits: the highlighter's own color when a highlight is
    /// selected or about to be drawn, otherwise the color shared by every other tool.
    /// </summary>
    private Color ActiveColor
    {
        get => UsesHighlightColor ? _highlightColor : _color;
        set
        {
            if (UsesHighlightColor)
                _highlightColor = value;
            else
                _color = value;
        }
    }

    private bool UsesHighlightColor =>
        _selected is HighlighterAnnotation || (_selected is null && _tool == Tool.Highlighter);

    // ---- Undo / redo -------------------------------------------------------------------

    private void Commit(object? coalesceKey = null)
    {
        _history.Commit(_document, coalesceKey);
        SyncHistoryButtons();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => StepHistory(undo: true);
    private void Redo_Click(object sender, RoutedEventArgs e) => StepHistory(undo: false);

    private void StepHistory(bool undo)
    {
        if (_drag is not null)
            return;
        CommitTextEdit();
        if (undo ? _history.Undo(_document) : _history.Redo(_document))
            Select(null); // the restored objects are copies; the old selection no longer exists
        SyncHistoryButtons();
    }

    private void SyncHistoryButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
    }

    // ---- Keyboard ----------------------------------------------------------------------

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // While typing, every key belongs to the text box.
        if (_editingText is not null || e.OriginalSource is TextBox)
            return;

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        int colorIndex = e.Key switch
        {
            >= VirtualKey.Number1 and <= VirtualKey.Number8 => e.Key - VirtualKey.Number1,
            >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad8 => e.Key - VirtualKey.NumberPad1,
            _ => -1,
        };

        bool handled = true;
        switch (e.Key)
        {
            // Zoom: Ctrl+= / Ctrl+- step, Ctrl+0 fits the window, Ctrl+1 shows actual pixels.
            case (VirtualKey)187 or VirtualKey.Add when ctrl: ZoomBy(ZoomStep, CanvasCenter); break;
            case (VirtualKey)189 or VirtualKey.Subtract when ctrl: ZoomBy(1 / ZoomStep, CanvasCenter); break;
            case VirtualKey.Number0 or VirtualKey.NumberPad0 when ctrl: FitToWindow(); break;
            case VirtualKey.Number1 or VirtualKey.NumberPad1 when ctrl: SetZoom(1, CanvasCenter); break;
            case VirtualKey.Z when ctrl: StepHistory(undo: !shift); break;
            case VirtualKey.Y when ctrl: StepHistory(undo: false); break;
            case var _ when colorIndex >= 0 && !ctrl: SetColor(Palette.Colors[colorIndex].Color); break;
            case VirtualKey.C when ctrl && shift: CopyText_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.P when ctrl: Pin_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.C when ctrl: Copy_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.S when ctrl: Save_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.V when !ctrl: SetTool(Tool.Select); break;
            case VirtualKey.A when !ctrl: SetTool(Tool.Arrow); break;
            case VirtualKey.R when !ctrl: SetTool(Tool.Rectangle); break;
            case VirtualKey.T when !ctrl: SetTool(Tool.Text); break;
            case VirtualKey.B when !ctrl: SetTool(Tool.Blur); break;
            case VirtualKey.N when !ctrl: SetTool(Tool.Step); break;
            case VirtualKey.S when !ctrl: SetTool(Tool.Spotlight); break;
            case VirtualKey.H when !ctrl: SetTool(Tool.Highlighter); break;
            case VirtualKey.P when !ctrl && PixelateButton.Visibility == Visibility.Visible:
                SetPixelate(PixelateButton.IsChecked != true);
                break;
            case (VirtualKey)219: SetWeight((int)WeightSlider.Value - 1); break; // [
            case (VirtualKey)221: SetWeight((int)WeightSlider.Value + 1); break; // ]
            case VirtualKey.Delete or VirtualKey.Back when _selected is not null:
                _document.Annotations.Remove(_selected);
                (_selected as IDisposable)?.Dispose();
                Select(null);
                Commit();
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
        if (_startAtTop && sender.ActualSize.Y > 0)
        {
            _startAtTop = false;
            if (TopView(sender) is Matrix3x2 top)
            {
                _view = top;
                _manualView = true;
                _wheelScrolls = true;
            }
        }

        // Until the user zooms or pans, keep the whole canvas fitted to the window.
        if (_drag is null && _editingText is null && !_manualView)
            _view = FitView(sender);
        UpdateZoomLabel();

        // Hard pixel edges when magnified (for inspecting pixels), smooth when shrunk.
        float zoom = Zoom;
        _document.ImageInterpolation = zoom > 1.5f ? CanvasImageInterpolation.NearestNeighbor
            : zoom < 1 ? CanvasImageInterpolation.HighQualityCubic
            : CanvasImageInterpolation.Linear;

        var ds = args.DrawingSession;
        ds.Transform = _view;

        if (_checkerBrush is not null)
            ds.FillRectangle(_document.Bounds, _checkerBrush);

        _document.Render(ds, skip: _editingText);
        _editingText?.Draw(ds, chromeOnly: true); // the text box draws the letters on top

        if (_selected is not null && _selected != _editingText)
            DrawSelection(ds, _selected);

        if (_textDrag is not null)
        {
            float px = 1 / _view.M11;
            using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            ds.DrawRectangle(new Windows.Foundation.Rect(_textDrag.Start.ToPoint(), _textDrag.End.ToPoint()), AccentColor, 1.5f * px, dashed);
        }
    }

    private void DrawSelection(CanvasDrawingSession ds, Annotation annotation)
    {
        float px = 1 / _view.M11;
        // Text has no handles, and a blur has no visible edge; outline both so the extent is clear.
        if (annotation.Handles.Count == 0 || annotation.IsAreaEffect)
        {
            using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            ds.DrawRectangle(annotation.Bounds.Inflate(2 * px), AccentColor, 1.5f * px, dashed);
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

    /// <summary>
    /// For tall (scrolling) captures: the top of the canvas, as wide as the window allows,
    /// rather than the whole thing shrunk to a sliver. Null when fitting it all is about as good.
    /// </summary>
    private Matrix3x2? TopView(CanvasControl canvas)
    {
        var bounds = _document.Bounds;
        var available = canvas.ActualSize - new Vector2(ViewPadding * 2);
        float rasterScale = (float)(canvas.XamlRoot?.RasterizationScale ?? 1.0);
        float scale = Math.Max(Math.Min(1 / rasterScale, available.X / (float)bounds.Width), 0.01f);
        if ((float)bounds.Height * scale <= available.Y * 1.5f)
            return null;

        float left = (canvas.ActualSize.X - (float)bounds.Width * scale) / 2;
        return Matrix3x2.CreateTranslation(-(float)bounds.X, -(float)bounds.Y)
             * Matrix3x2.CreateScale(scale)
             * Matrix3x2.CreateTranslation(left, ViewPadding);
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

        // Middle-drag, or Space + drag, pans the view.
        if (point.Properties.IsMiddleButtonPressed || (_spaceHeld && point.Properties.IsLeftButtonPressed))
        {
            CommitTextEdit();
            _pan = (e.Pointer.PointerId, point.Position.ToVector2());
            CanvasHost.CapturePointer(e.Pointer);
            CanvasHost.Cursor = _moveCursor;
            e.Handled = true;
            return;
        }

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
        else if (_document.HitTest(p, tolerance, GrabsAreaEffects) is Annotation hit)
        {
            // Clicking an existing object grabs it, whichever tool is active.
            Select(hit);
            if (hit is TextAnnotation text && _tool == Tool.Text)
                BeginTextEdit(text, isNew: false);
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
            // Text is created on release: a click places it at the default size, while a
            // dragged box sets the size and wrapping width.
            _textDrag = new TextDrag(e.Pointer.PointerId, p) { End = p };
            CanvasHost.CapturePointer(e.Pointer);
            return;
        }

        if (_tool == Tool.Step)
        {
            // Placed on press; dragging before release moves it into position.
            var step = new StepAnnotation(p, _color, weight, Unit);
            _document.Annotations.Add(step);
            Select(step);
            StartDrag(e, step, handle: -1, creating: true, p);
            return;
        }

        TwoPointAnnotation shape = _tool switch
        {
            Tool.Arrow => new ArrowAnnotation(p, _color, weight, Unit),
            Tool.Blur => new BlurAnnotation(p, _document.Image, _document.ImageBounds, _pixelate, weight, Unit),
            Tool.Spotlight => new SpotlightAnnotation(p, _document.ImageBounds, weight, Unit),
            Tool.Highlighter => new HighlighterAnnotation(p, _document.ImageBounds, _highlightColor, weight, Unit),
            _ => new RectangleAnnotation(p, _color, weight, Unit),
        };
        _document.Annotations.Add(shape);
        Select(shape);
        StartDrag(e, shape, TwoPointAnnotation.EndHandle, creating: true, p);
    }

    private void CreateText(TextDrag drag)
    {
        var text = new TextAnnotation(drag.Start, _color, _toolWeights[Tool.Text], Unit) { Boxed = _textBoxed };
        var box = new Windows.Foundation.Rect(drag.Start.ToPoint(), drag.End.ToPoint());
        float minBox = 12 / _view.M11; // a few screen pixels of wobble still counts as a click

        if (box.Width >= minBox && box.Height >= minBox)
        {
            // Size the font so one line fills the box's height, including the background
            // box's padding when there is one, and wrap at its width.
            float lineRatio = TextAnnotation.LineHeightRatio + (text.Boxed ? 0.3f : 0);
            float size = Math.Clamp((float)box.Height / lineRatio, 6 * Unit, 400 * Unit);
            text.FontSizeOverride = size;
            var pad = text.Boxed ? new Vector2(size * 0.35f, size * 0.15f) : Vector2.Zero;
            text.Position = new Vector2((float)box.X, (float)box.Y) + pad;
            text.WrapWidth = Math.Max(size, (float)box.Width - pad.X * 2);
        }
        else
        {
            // Put the click at the middle of the first line, where the caret appears.
            text.Position -= new Vector2(0, text.FontSize * 0.65f);
        }

        _document.Annotations.Add(text);
        Select(text);
        BeginTextEdit(text, isNew: true);
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
        if (_pan is { } pan && e.Pointer.PointerId == pan.PointerId)
        {
            var position = e.GetCurrentPoint(Canvas).Position.ToVector2();
            _view.Translation += position - pan.Last;
            _pan = (pan.PointerId, position);
            _manualView = true;
            Canvas.Invalidate();
            e.Handled = true;
            return;
        }

        var p = ToDocument(e.GetCurrentPoint(Canvas).Position.ToVector2());

        if (_textDrag is not null && e.Pointer.PointerId == _textDrag.PointerId)
        {
            _textDrag.End = p;
            Canvas.Invalidate();
            e.Handled = true;
            return;
        }

        if (_drag is null || e.Pointer.PointerId != _drag.PointerId)
        {
            UpdateCursor(p);
            return;
        }

        if (_drag.Handle >= 0)
            _drag.Target.MoveHandle(_drag.Handle, p);
        else
            _drag.Target.MoveBy(p - _drag.Last);
        _drag.Moved |= p != _drag.Last;
        _drag.Last = p;

        Canvas.Invalidate();
        e.Handled = true;
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pan is { } pan && e.Pointer.PointerId == pan.PointerId)
        {
            _pan = null;
            CanvasHost.ReleasePointerCapture(e.Pointer);
            CanvasHost.Cursor = _spaceHeld ? _panCursor : null;
            UpdateCursor(null);
            e.Handled = true;
            return;
        }

        if (_textDrag is not null && e.Pointer.PointerId == _textDrag.PointerId)
        {
            var drag = _textDrag;
            _textDrag = null;
            CanvasHost.ReleasePointerCapture(e.Pointer);
            CreateText(drag);
            e.Handled = true;
            return;
        }

        if (_drag is null || e.Pointer.PointerId != _drag.PointerId)
            return;

        // A click without a drag shouldn't leave an invisible zero-size shape behind.
        if (_drag.Creating && _drag.Target is TwoPointAnnotation shape &&
            Math.Abs(shape.End.X - shape.Start.X) < MinShapeSize && Math.Abs(shape.End.Y - shape.Start.Y) < MinShapeSize)
        {
            _document.Annotations.Remove(shape);
            Select(null);
        }
        else if (_drag.Moved || _drag.Creating)
        {
            Commit();
        }

        _drag = null;
        CanvasHost.ReleasePointerCapture(e.Pointer);
        Canvas.Invalidate();
        e.Handled = true;
    }

    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var p = ToDocument(e.GetPosition(Canvas).ToVector2());
        if (_document.HitTest(p, HitTolerance / _view.M11, includeAreaEffects: false) is TextAnnotation text)
        {
            Select(text);
            BeginTextEdit(text, isNew: false);
            e.Handled = true;
        }
    }

    // ---- Zoom and pan ------------------------------------------------------------------

    private const float ZoomStep = 1.25f;
    private const float MinZoom = 0.05f;
    private const float MaxZoom = 32f;

    /// <summary>Set once the user zooms or pans; until then the canvas auto-fits the window.</summary>
    private bool _manualView;
    /// <summary>A scrolling capture opens at its top; set until its first draw.</summary>
    private bool _startAtTop;
    /// <summary>For tall captures the wheel scrolls (as in a document) and Ctrl+wheel zooms.</summary>
    private bool _wheelScrolls;
    private (uint PointerId, Vector2 Last)? _pan;
    private bool _spaceHeld;
    private readonly InputCursor _panCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    private float RasterScale => (float)(Canvas.XamlRoot?.RasterizationScale ?? 1.0);

    /// <summary>Screen pixels per image pixel: 1 is "actual pixels" (100%).</summary>
    private float Zoom => _view.M11 * RasterScale;

    private Vector2 CanvasCenter => Canvas.ActualSize / 2;

    private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Canvas);
        int delta = point.Properties.MouseWheelDelta;
        if (point.Properties.IsHorizontalMouseWheel)
        {
            // Tilt wheels and sideways touchpad swipes pan instead.
            _view.Translation -= new Vector2(delta, 0);
            _manualView = true;
            Canvas.Invalidate();
        }
        else if (_wheelScrolls && !e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            // Touchpad pinches arrive as Ctrl+wheel, so they still zoom.
            _view.Translation += new Vector2(0, delta);
            _manualView = true;
            Canvas.Invalidate();
        }
        else if (_drag is null && _textDrag is null)
        {
            ZoomBy(MathF.Pow(ZoomStep, delta / 120f), point.Position.ToVector2());
        }
        e.Handled = true;
    }

    private void ZoomBy(float factor, Vector2 anchor)
    {
        float current = Zoom;
        float target = Math.Clamp(current * factor, MinZoom, MaxZoom);
        // Land exactly on 100% when passing through it, so pixels line up.
        if ((current < 1 && target > 1) || (current > 1 && target < 1))
            target = 1;
        SetZoom(target, anchor);
    }

    /// <summary>Zooms to <paramref name="zoom"/>, keeping the point under <paramref name="anchor"/> (control DIPs) in place.</summary>
    private void SetZoom(float zoom, Vector2 anchor)
    {
        CommitTextEdit(); // the inline text box is positioned for the old zoom
        var pinned = ToDocument(anchor);
        float scale = zoom / RasterScale;
        _view = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(anchor - pinned * scale);
        _manualView = true;
        Canvas.Invalidate();
    }

    private void FitToWindow()
    {
        CommitTextEdit();
        _manualView = false;
        Canvas.Invalidate();
    }

    /// <summary>The zoom pill: fit when zoomed, actual pixels when fitted.</summary>
    private void ZoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (_manualView)
            FitToWindow();
        else
            SetZoom(1, CanvasCenter);
    }

    private void UpdateZoomLabel()
    {
        string text = $"{Math.Round(Zoom * 100)}%";
        if (ZoomButton.Content as string != text)
            ZoomButton.Content = text;
    }

    // Space is caught on the way down (preview), so it pans instead of clicking a focused button.
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || _editingText is not null || e.OriginalSource is TextBox)
            return;
        if (!_spaceHeld)
        {
            _spaceHeld = true;
            CanvasHost.Cursor = _panCursor;
        }
        e.Handled = true;
    }

    private void Root_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || !_spaceHeld)
            return;
        _spaceHeld = false;
        UpdateCursor(null);
        e.Handled = true;
    }

    // ---- Cursor ------------------------------------------------------------------------

    private readonly InputCursor _arrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private readonly InputCursor _crossCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private readonly InputCursor _textCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private readonly InputCursor _moveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private readonly InputCursor _handleCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    /// <summary>Blur and spotlight can only be grabbed with Select or their own tool.</summary>
    private bool GrabsAreaEffects => _tool is Tool.Select or Tool.Blur or Tool.Spotlight;

    /// <summary>
    /// Crosshair for drawing tools, I-beam for text, arrow for select; over something that
    /// can be grabbed, a move cursor (or a hand over a resize handle).
    /// </summary>
    /// <param name="point">The pointer in document space, or null if unknown.</param>
    private void UpdateCursor(Vector2? point)
    {
        if (_spaceHeld || _pan is not null)
            return; // the pan cursor wins while panning is armed

        var cursor = _tool switch
        {
            Tool.Select => _arrowCursor,
            Tool.Text => _textCursor,
            _ => _crossCursor,
        };

        if (point is Vector2 p)
        {
            if (_selected is not null && HitHandle(_selected, p) is not null)
                cursor = _handleCursor;
            else if (_document.HitTest(p, HitTolerance / _view.M11, GrabsAreaEffects) is Annotation hit)
                cursor = hit is TextAnnotation && _tool == Tool.Text ? _textCursor : _moveCursor;
        }
        CanvasHost.Cursor = cursor;
    }

    // ---- Inline text editing -----------------------------------------------------------

    private void BeginTextEdit(TextAnnotation text, bool isNew)
    {
        CommitTextEdit();
        _editingText = text;
        _editingIsNew = isNew;
        _textBeforeEdit = text.Text;

        float scale = _view.M11;
        var color = new SolidColorBrush(text.TextColor);
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
        if (text.WrapWidth is float wrap)
        {
            box.Width = wrap * scale;
            box.TextWrapping = TextWrapping.Wrap;
        }
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

        bool removed = string.IsNullOrWhiteSpace(text.Text);
        if (removed)
        {
            _document.Annotations.Remove(text);
            text.Dispose();
            if (_selected == text)
                Select(null);
        }

        // The whole typing session is one undo step (the text box has its own undo while typing).
        bool changed = _editingIsNew ? !removed : removed || text.Text != _textBeforeEdit;
        if (changed)
            Commit();
        Canvas.Invalidate();
    }

    // ---- Export ------------------------------------------------------------------------

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        var png = await _document.EncodePngAsync();
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(png));
        // The bitmap format drops transparency (rounded window corners, unfilled canvas);
        // most apps that paste images look for PNG first and keep it.
        package.SetData("PNG", png.CloneStream());
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    /// <summary>Reads the text in the original screenshot (annotations ignored) and copies it.</summary>
    private async void CopyText_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        var size = _document.Image.SizeInPixels;
        string message = await TextRecognizer.CopyToClipboardAsync(
            _document.Image.GetPixelBytes(), (int)size.Width, (int)size.Height, _document.SourceScale);

        var window = new System.Drawing.Rectangle(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        Shell.Hud.Show(message, window, Content.XamlRoot.RasterizationScale);
    }

    /// <summary>
    /// Floats the finished image above other windows, exactly where it was captured, and
    /// closes the editor.
    /// </summary>
    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        using var png = await _document.EncodePngAsync();
        using var stream = png.AsStreamForRead();
        System.Drawing.Bitmap image;
        using (var decoded = new System.Drawing.Bitmap(stream))
            image = new System.Drawing.Bitmap(decoded); // detach from the stream

        // The canvas can extend past the capture (annotations, backdrop); offset so the
        // screenshot itself lands back on the pixels it came from.
        var bounds = _document.Bounds;
        var location = new System.Drawing.Point(_desktopBounds.X + (int)bounds.X, _desktopBounds.Y + (int)bounds.Y);
        new Shell.PinWindow(image, location, _document.SourceScale, App.OpenEditor).Show();
        Close();
    }

    // ---- Beautify ----------------------------------------------------------------------

    private void BuildBackdropControls()
    {
        for (int i = 0; i < BackdropPresets.All.Length; i++)
        {
            int index = i;
            var (name, from, to) = BackdropPresets.All[i];
            var swatch = new Microsoft.UI.Xaml.Controls.Button
            {
                Width = 52,
                Height = 36,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = from, Offset = 0 },
                        new GradientStop { Color = to, Offset = 1 },
                    },
                },
                BorderBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            };
            swatch.Resources["ButtonBackgroundPointerOver"] = swatch.Background;
            swatch.Resources["ButtonBackgroundPressed"] = swatch.Background;
            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += (_, _) =>
            {
                _backdropPreset = index;
                BeautifyButton.IsChecked = true; // turns beautify on if it was off
                ApplyBackdrop();
            };
            _backdropSwatches.Add(swatch);
            BackdropSwatches.Children.Add(swatch);
        }

        PaddingSlider.Value = _backdropPadding;
        // Hooked up after setting the initial value, so loading doesn't count as a change.
        PaddingSlider.ValueChanged += (_, e) =>
        {
            _backdropPadding = e.NewValue;
            ApplyBackdrop();
        };
        BeautifyButton.IsChecked = _beautify;
        ApplyBackdrop();
    }

    private void BeautifyButton_IsCheckedChanged(ToggleSplitButton sender, ToggleSplitButtonIsCheckedChangedEventArgs args)
    {
        _beautify = sender.IsChecked;
        sender.Content = Shell.FluentIcons.Create("sparkle", filled: sender.IsChecked);
        ApplyBackdrop();
    }

    // ---- Toolbar icons -----------------------------------------------------------------

    private static readonly Dictionary<string, string> ToolIcons = new()
    {
        [nameof(Tool.Select)] = "cursor",
        [nameof(Tool.Arrow)] = "arrow_up_right",
        [nameof(Tool.Rectangle)] = "rectangle_landscape",
        [nameof(Tool.Text)] = "text_t",
        [nameof(Tool.Blur)] = "blur",
        [nameof(Tool.Step)] = "number_circle_1",
        [nameof(Tool.Highlighter)] = "highlight",
        [nameof(Tool.Spotlight)] = "flashlight",
    };

    /// <summary>
    /// Gives every toolbar button its Fluent icon. Like Windows 11's own toolbars, icons are
    /// outlined normally and filled for the active tool or toggle, and while hovered.
    /// </summary>
    private void InitializeIcons()
    {
        BindIcon(SaveButton, "save");
        BindIcon(CopyButton, "copy");
        BindIcon(UndoButton, "arrow_undo");
        BindIcon(RedoButton, "arrow_redo");
        foreach (var button in Toolbar.PrimaryCommands.OfType<AppBarToggleButton>())
        {
            if (button.Tag is string tool && ToolIcons.TryGetValue(tool, out var icon))
                BindIcon(button, icon);
        }
        BindIcon(PixelateButton, "grid_dots");
        BindIcon(TextBackgroundButton, "color_background");
        BindIcon(PinButton, "pin");
        BindIcon(CopyTextButton, "scan_text");
        BeautifyButton.Content = Shell.FluentIcons.Create("sparkle", filled: false);
    }

    private void BindIcon(AppBarButton button, string icon)
    {
        button.Icon = Shell.FluentIcons.Create(icon, filled: false);
        button.PointerEntered += (_, _) => button.Icon = Shell.FluentIcons.Create(icon, filled: button.IsEnabled);
        button.PointerExited += (_, _) => button.Icon = Shell.FluentIcons.Create(icon, filled: false);
    }

    private void BindIcon(AppBarToggleButton button, string icon)
    {
        bool hovered = false;
        void Refresh() => button.Icon = Shell.FluentIcons.Create(icon, filled: hovered || button.IsChecked == true);
        button.PointerEntered += (_, _) => { hovered = true; Refresh(); };
        button.PointerExited += (_, _) => { hovered = false; Refresh(); };
        button.Checked += (_, _) => Refresh();
        button.Unchecked += (_, _) => Refresh();
        Refresh();
    }

    private void ApplyBackdrop()
    {
        var (_, from, to) = BackdropPresets.All[_backdropPreset];
        _document.Backdrop = _beautify ? new Backdrop(from, to, (float)_backdropPadding) : null;
        for (int i = 0; i < _backdropSwatches.Count; i++)
            _backdropSwatches[i].BorderThickness = new Thickness(i == _backdropPreset ? 2 : 0);
        Canvas.Invalidate();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = $"Windshot {DateTime.Now:yyyy-MM-dd HHmmss}",
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
