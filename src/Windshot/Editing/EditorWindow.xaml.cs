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
    Crop,
    /// <summary>Not a tool on the toolbar: inserted images (Insert image, paste or drop), for their options and style.</summary>
    Image,
}

public sealed partial class EditorWindow : Window
{
    private const float ViewPadding = 32;
    private const float HitTolerance = 6;
    private const float HandleRadius = 5;
    private const float MinShapeSize = 3;

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
    private HighlightBlend _highlightBlend;
    private bool _textBoxed;
    private string _font;
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

    /// <param name="Handle">
    /// Index into <see cref="Annotation.Handles"/>, -1 (<see cref="MoveWhole"/>) to move the whole
    /// object, or <see cref="RotateHandle"/> to turn it.
    /// </param>
    /// <param name="Start">Where the drag began, in document coordinates.</param>
    private sealed record DragState(uint PointerId, Annotation Target, int Handle, bool Creating, Vector2 Start)
    {
        /// <summary>What a move drags: the object, or the whole group it's part of.</summary>
        public IReadOnlyList<Annotation> Movers { get; init; } = [Target];

        /// <summary>How far the whole object has been moved so far (Shift keeps it to one axis).</summary>
        public Vector2 Moved { get; set; }

        /// <summary>Whether anything changed; a click that only selects shouldn't add an undo step.</summary>
        public bool Changed { get; set; }

        /// <summary>Width over height kept by a Shift-drag of a box's corner.</summary>
        public float Aspect { get; init; } = 1;

        /// <summary>
        /// For turning: the point turned about, the pointer's angle around it at the start, the
        /// object's own angle at the start, and how far it's turned so far.
        /// </summary>
        public Vector2 Pivot { get; init; }
        public float StartAngle { get; init; }
        public float FromAngle { get; init; }
        public float Turned { get; set; }
    }

    /// <summary>An icon-only toolbar button's width, in DIPs: no wider than its highlight, plus a little gap.</summary>
    private const double IconButtonWidth = 36;

    private const int MoveWhole = -1;
    private const int RotateHandle = -2;
    /// <summary>How far above the top edge the rotate handle sits, in DIPs.</summary>
    private const float RotateHandleReach = 28;
    /// <summary>Shift-turning steps by this much, in degrees.</summary>
    private const float RotateStep = 15;

    /// <summary>A text box being dragged out with the text tool (document coordinates).</summary>
    private sealed record TextDrag(uint PointerId, Vector2 Start)
    {
        public Vector2 End { get; set; }
    }

    internal EditorWindow(CapturedImage capture)
    {
        InitializeComponent();
        InitializeIcons();
        ApplyTheme();
        _document = new Document(capture);
        _desktopBounds = capture.DesktopBounds;
        _startAtTop = capture.IsScrolling;
        _history = new History(_document);

        var prefs = Settings.Current;
        _toolWeights = new Dictionary<Tool, int>(prefs.Editor.Sizes);
        // Remembered colors from another color set become their counterparts in this one.
        _color = Themes.InSet(ColorExtensions.TryParseHex(prefs.Editor.Color, out var color) ? color : _palette[0].Color, _palette);
        _highlightColor = Themes.InSet(ColorExtensions.TryParseHex(prefs.Editor.HighlighterColor, out var highlight) ? highlight : _palette[2].Color, _palette);
        _pixelate = prefs.Editor.Pixelate;
        _highlightBlend = prefs.Editor.HighlighterBlend;
        _shapeKind = prefs.Editor.Shape;
        _fillShapes = prefs.Editor.FillShapes;
        if (Stickers.All.Any(s => s.Icon == prefs.Editor.Sticker))
            _sticker = prefs.Editor.Sticker;
        _textBoxed = prefs.Editor.TextBackground;
        _font = string.IsNullOrWhiteSpace(prefs.Editor.Font) ? TextAnnotation.DefaultFontFamily : prefs.Editor.Font;
        _beautify = prefs.Beautify.OnByDefault;
        _backdropPreset = Math.Max(0, Array.FindIndex(BackdropPresets.All,
            p => p.Name.Equals(prefs.Beautify.Preset, StringComparison.OrdinalIgnoreCase)));
        _backdropPadding = Math.Clamp(prefs.Beautify.Padding, 16, 160);

        // Hooked up here rather than in XAML: setting Minimum during load fires ValueChanged
        // before the rest of the window exists.
        WeightSlider.ValueChanged += WeightSlider_ValueChanged;
        InitializeColorSets();
        InitializeFonts();
        InitializeBlend();
        InitializeShapes();
        InitializeLayerStyle();
        InitializeMoreColors();
        BuildSwatches();
        BuildBackdropControls();
        InitializeLayers();
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
        prefs.Editor.HighlighterBlend = _highlightBlend;
        prefs.Editor.Shape = _shapeKind;
        prefs.Editor.FillShapes = _fillShapes;
        prefs.Editor.Sticker = _sticker;
        prefs.Editor.TextBackground = _textBoxed;
        prefs.Editor.Font = _font;
        prefs.Editor.Styles = _toolStyles.Where(entry => entry.Value != LayerStyle.None).ToDictionary(entry => entry.Key, entry => entry.Value);
        prefs.Editor.ShowLayers = _layersOpen;
        prefs.Beautify.Preset = BackdropPresets.All[_backdropPreset].Name;
        prefs.Beautify.Padding = _backdropPadding;
        Settings.Save();
    }

    private void SizeToCapture(CapturedImage capture)
    {
        // Open on the monitor the capture came from (most of it, if it spans monitors), showing
        // it at 1:1 physical pixels, capped to most of that monitor's work area.
        var source = capture.DesktopBounds;
        var workArea = DisplayArea.GetFromRect(
            new RectInt32(source.X, source.Y, source.Width, source.Height), DisplayAreaFallback.Primary).WorkArea;
        // Move onto that monitor before sizing: crossing to a monitor with a different scale
        // rescales the window, which would undo a size set first.
        AppWindow.Move(new PointInt32(workArea.X, workArea.Y));
        int chromeWidth = (int)(2 * ViewPadding * capture.Scale + 120);
        int chromeHeight = (int)((2 * ViewPadding + 122) * capture.Scale);
        // The minimum keeps the whole toolbar visible.
        int width = Math.Clamp(capture.Width + chromeWidth, (int)(1060 * capture.Scale), (int)(workArea.Width * 0.85));
        int height = Math.Clamp(capture.Height + chromeHeight, (int)(360 * capture.Scale), (int)(workArea.Height * 0.85));
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2));
        _windowScale = capture.Scale;
        ApplyMinimumSize();
        Root.Loaded += (_, _) => Root.XamlRoot.Changed += (_, _) => ApplyMinimumSize();
    }

    /// <summary>The window's display scale; follows it between monitors.</summary>
    private double _windowScale = 1;

    /// <summary>
    /// Keeps the window wide enough for the toolbar's essentials (the rest overflow into
    /// "…") and, with the layers panel open, for the panel plus a usable canvas beside it.
    /// Opening the panel in a narrower window widens the window to fit.
    /// </summary>
    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        if (Root.XamlRoot is { } root)
            _windowScale = root.RasterizationScale;
        int minWidth = (int)((_layersOpen ? 640 : 480) * _windowScale);
        presenter.PreferredMinimumWidth = minWidth;
        presenter.PreferredMinimumHeight = (int)(320 * _windowScale);

        if (AppWindow.Size.Width >= minWidth || presenter.State != OverlappedPresenterState.Restored)
            return;
        // Grow to the right, but stay on the screen.
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int x = Math.Max(workArea.X, Math.Min(AppWindow.Position.X, workArea.X + workArea.Width - minWidth));
        AppWindow.MoveAndResize(new RectInt32(x, AppWindow.Position.Y, minWidth, AppWindow.Size.Height));
    }

    // ---- Tools and size ----------------------------------------------------------------

    private void SetTool(Tool tool)
    {
        CommitTextEdit();
        FinishPolygon();
        // Leaving the crop tool applies the crop; entering it shows the whole canvas to crop from.
        if (_tool == Tool.Crop && tool != Tool.Crop)
            ApplyCrop();
        else if (tool == Tool.Crop && _tool != Tool.Crop)
            BeginCrop();
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
            SyncBlend();
            SyncTextBackground();
            SyncFont();
            SyncShapes();
            SyncColor();
        }
        SyncOptions();
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
        ImageAnnotation => Tool.Image,
        _ => Tool.Rectangle,
    };

    private void Select(Annotation? annotation)
    {
        _selected = annotation;
        _group.Clear();
        _imageLayerSelected = false;
        SyncLayerSelection();
        if (annotation is { HasColor: true })
            ActiveColor = annotation.Color; // picking up an object's color makes it easy to match
        SyncSlider();
        SyncColor();
        SyncPixelate();
        SyncBlend();
        SyncTextBackground();
        SyncFont();
        SyncShapes();
        SyncOptions();
        Canvas.Invalidate();
    }

    /// <summary>The text background toggle (on the options bar for text).</summary>
    private void SyncTextBackground()
    {
        var text = _selected as TextAnnotation;
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
        SyncLayerStyle(); // a text background has corners to round
        Canvas.Invalidate();
    }

    /// <summary>The Pixelate toggle (on the options bar for blur).</summary>
    private void SyncPixelate()
    {
        var blur = _selected as BlurAnnotation;
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
                // A sticker's size is its box: the setting resizes it around its center.
                if (_selected is ShapeAnnotation { Kind: ShapeKind.Sticker } sticker)
                {
                    var center = (sticker.Start + sticker.End) / 2;
                    var half = new Vector2(StickerSide(weight) / 2);
                    sticker.Start = center - half;
                    sticker.End = center + half;
                }
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

    private void InitializeColorSets()
    {
        // Added as items rather than bound: binding a .NET list fails in the trimmed build.
        foreach (var theme in Themes.All)
            ColorSetPicker.Items.Add(theme.Name);
        ColorSetPicker.SelectedIndex = Array.FindIndex(Themes.All, t => t.Colors == _palette);
        ColorSetPicker.SelectionChanged += (_, _) =>
        {
            if (ColorSetPicker.SelectedIndex >= 0)
                SetColorSet(Themes.All[ColorSetPicker.SelectedIndex]);
        };
    }

    /// <summary>
    /// Offers another theme's colors, and moves the current colors and everything drawn to
    /// their counterparts in it (Coral becomes Chili, and so on), as one undo step.
    /// </summary>
    private void SetColorSet(Theme theme)
    {
        if (theme.Colors == _palette)
            return;
        CommitTextEdit();
        _palette = theme.Colors;
        _color = Themes.InSet(_color, _palette);
        _highlightColor = Themes.InSet(_highlightColor, _palette);
        bool recolored = false;
        foreach (var annotation in _document.Annotations.Where(a => a.HasColor))
        {
            var color = Themes.InSet(annotation.Color, _palette);
            if (color != annotation.Color)
            {
                annotation.Color = color;
                recolored = true;
            }
        }
        if (recolored)
            Commit();
        // Remembered like the other editor preferences, for the next editors too.
        Settings.Current.Appearance.Colors = theme.Name;
        BuildSwatches();
        Canvas.Invalidate();
    }

    private void BuildSwatches()
    {
        _swatches.Clear();
        SwatchPanel.Children.Clear();
        for (int i = 0; i < _palette.Length; i++)
        {
            var (name, color) = _palette[i];
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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, name); // for screen readers
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
        // A group's blurs, spotlights and images have no color of their own; everything else takes it.
        var recolor = Selection.Where(a => a.HasColor && a.Color != color).ToList();
        foreach (var annotation in recolor)
            annotation.Color = color;
        if (recolor.Count > 0)
            Commit(coalesceKey: ("color", _group.Count > 0 ? _groupKey : _selected));
        SyncColor();
        Canvas.Invalidate();
    }

    private void SyncColor()
    {
        var color = ActiveColor;
        ColorSwatch.Fill = new SolidColorBrush(color);
        for (int i = 0; i < _swatches.Count; i++)
            _swatches[i].BorderThickness = new Thickness(_palette[i].Color == color ? 3 : 1);
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
        RefreshLayers();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => StepHistory(undo: true);
    private void Redo_Click(object sender, RoutedEventArgs e) => StepHistory(undo: false);

    private void StepHistory(bool undo)
    {
        if (_drag is not null)
            return;
        if (_polygonDraft is not null && undo)
        {
            UndoPolygonCorner();
            return;
        }
        FinishPolygon();
        if (_tool == Tool.Crop)
        {
            // While cropping, undo just drops the crop being adjusted.
            CancelCrop();
            SetTool(Tool.Select);
            return;
        }
        CommitTextEdit();
        if (undo ? _history.Undo(_document) : _history.Redo(_document))
            Select(null); // the restored objects are copies; the old selection no longer exists
        SyncHistoryButtons();
        RefreshLayers();
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
            case var _ when colorIndex >= 0 && !ctrl: SetColor(_palette[colorIndex].Color); break;
            case VirtualKey.C when ctrl && shift: CopyText_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.P when ctrl: Pin_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.C when ctrl: Copy_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.S when ctrl && shift: Save(ask: true); break;
            case VirtualKey.S when ctrl: Save_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.C when !ctrl && !shift: SetTool(Tool.Crop); break;
            case VirtualKey.V when ctrl: PasteImage(); break;
            case VirtualKey.V when !ctrl: SetTool(Tool.Select); break;
            case VirtualKey.A when !ctrl: SetTool(Tool.Arrow); break;
            case VirtualKey.R when !ctrl: SetTool(Tool.Rectangle); break;
            case VirtualKey.T when !ctrl: SetTool(Tool.Text); break;
            case VirtualKey.B when !ctrl: SetTool(Tool.Blur); break;
            case VirtualKey.N when !ctrl: SetTool(Tool.Step); break;
            case VirtualKey.S when !ctrl: SetTool(Tool.Spotlight); break;
            case VirtualKey.H when !ctrl: SetTool(Tool.Highlighter); break;
            case VirtualKey.F when !ctrl && FillButton.Visibility == Visibility.Visible:
                SetFill(FillButton.IsChecked != true);
                break;
            case VirtualKey.P when !ctrl && PixelateButton.Visibility == Visibility.Visible:
                SetPixelate(PixelateButton.IsChecked != true);
                break;
            // Ctrl+] / Ctrl+[ restack the selection (Shift: to the top or bottom); plain ] and [ resize.
            case (VirtualKey)219 when ctrl: MoveLayer(-1, allTheWay: shift); break;
            case (VirtualKey)221 when ctrl: MoveLayer(+1, allTheWay: shift); break;
            case (VirtualKey)219: SetWeight((int)WeightSlider.Value - 1); break; // [
            case (VirtualKey)221: SetWeight((int)WeightSlider.Value + 1); break; // ]
            case VirtualKey.L when !ctrl: SetLayersOpen(!_layersOpen); break;
            case VirtualKey.Delete or VirtualKey.Back when Selection.Count > 0: DeleteSelected(); break;
            case VirtualKey.Escape when Selection.Count > 0: Select(null); break;
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
        _editingText?.DrawChrome(ds); // the text box draws the letters on top

        if (_selected is { Visible: true } && _selected != _editingText)
            DrawSelection(ds, _selected);
        foreach (var member in _group.Where(a => a.Visible))
            DrawOutline(ds, member); // selected together: outlined, without handles
        if (_cropRect is { } crop)
            DrawCropOverlay(ds, crop);

        if (_textDrag is not null)
        {
            float px = 1 / _view.M11;
            using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            ds.DrawRectangle(new Windows.Foundation.Rect(_textDrag.Start.ToPoint(), _textDrag.End.ToPoint()), AccentColor, 1.5f * px, dashed);
        }
        DrawPolygonDraft(ds);
    }

    private void DrawSelection(CanvasDrawingSession ds, Annotation annotation)
    {
        float px = 1 / _view.M11;
        // Text has no edge of its own, and neither does a blur; outline both so the extent is clear.
        if (annotation.Handles.Count == 0 || annotation.IsAreaEffect || annotation is TextAnnotation)
            DrawOutline(ds, annotation);

        // The rotate handle, on a stalk from the middle of the top edge.
        var (stalk, rotate) = RotateHandleAt(annotation);
        ds.DrawLine(stalk, rotate, AccentColor, 1.5f * px);
        foreach (var handle in annotation.Handles.Append(rotate))
        {
            ds.FillCircle(handle, HandleRadius * px, Color.FromArgb(255, 255, 255, 255));
            ds.DrawCircle(handle, HandleRadius * px, AccentColor, 1.5f * px);
        }
    }

    /// <summary>A dashed outline around the object, turned with it so it hugs it.</summary>
    private void DrawOutline(CanvasDrawingSession ds, Annotation annotation)
    {
        float px = 1 / _view.M11;
        using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        using var outline = CanvasGeometry.CreatePolygon(ds, annotation.Corners(annotation.Frame.Inflate(2 * px)));
        ds.DrawGeometry(outline, AccentColor, 1.5f * px, dashed);
    }

    /// <summary>The rotate handle (and the foot of its stalk): above the middle of the top edge, turned with the object.</summary>
    private (Vector2 Stalk, Vector2 Handle) RotateHandleAt(Annotation annotation)
    {
        var frame = annotation.Frame;
        var stalk = annotation.ToCanvas(new Vector2((float)(frame.X + frame.Width / 2), (float)frame.Top));
        var up = Vector2.TransformNormal(-Vector2.UnitY, Matrix3x2.CreateRotation(annotation.Angle));
        return (stalk, stalk + up * (RotateHandleReach / _view.M11));
    }

    /// <summary>The middle of the object, which turning goes around.</summary>
    private static Vector2 MiddleOf(Annotation annotation)
    {
        var frame = annotation.Frame;
        return annotation.ToCanvas(new Vector2((float)(frame.X + frame.Width / 2), (float)(frame.Y + frame.Height / 2)));
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
            CanvasHost.Cursor = PanningCursor;
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

        if (_polygonDraft is not null)
        {
            PolygonPressed(p, Shifted(e));
            return;
        }

        if (_tool == Tool.Crop)
        {
            CropPressed(e, p);
            return;
        }

        // Ctrl-click adds an object to the selection, or takes it out.
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            if (_document.HitTest(p, tolerance, includeAreaEffects: true) is Annotation picked)
                ToggleSelected(picked);
            return;
        }

        if (_selected is not null && HitHandle(_selected, p) is int handle)
        {
            StartDrag(e, _selected, handle, creating: false, p);
        }
        else if (GrabAt(p, tolerance) is Annotation hit)
        {
            // Clicking an existing object grabs it, whichever tool is active; text too, so
            // it can be moved with the text tool. Double-clicking text edits it. Grabbing one
            // of a group moves the whole group.
            if (!_group.Contains(hit))
                Select(hit);
            StartDrag(e, hit, MoveWhole, creating: false, p);
        }
        else
        {
            CreateAt(e, p);
        }
    }

    /// <summary>
    /// What a click grabs: the topmost object there, except that blurs and spotlights need the
    /// right tool (see <see cref="GrabsAreaEffects"/>), or to be part of the group selected.
    /// </summary>
    private Annotation? GrabAt(Vector2 p, float tolerance)
    {
        var hit = _document.HitTest(p, tolerance, GrabsAreaEffects);
        if (_group.Count > 0 && (hit is null || !_group.Contains(hit)) &&
            _document.HitTest(p, tolerance, includeAreaEffects: true) is { } member && _group.Contains(member))
            return member;
        return hit;
    }

    private void CreateAt(PointerRoutedEventArgs e, Vector2 p)
    {
        if (_tool == Tool.Select)
        {
            Select(null);
            return;
        }

        if (_tool == Tool.Rectangle && _shapeKind == ShapeKind.Polygon)
        {
            StartPolygon(p);
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
            AddLayer(step);
            Select(step);
            StartDrag(e, step, MoveWhole, creating: true, p);
            return;
        }

        TwoPointAnnotation shape = _tool switch
        {
            Tool.Arrow => new ArrowAnnotation(p, _color, weight, Unit),
            Tool.Blur => new BlurAnnotation(p, _pixelate, weight, Unit),
            Tool.Spotlight => new SpotlightAnnotation(p, weight, Unit),
            Tool.Highlighter => new HighlighterAnnotation(p, _highlightColor, weight, Unit) { Blend = _highlightBlend },
            _ => new ShapeAnnotation(p, _shapeKind, _color, weight, Unit) { Filled = _fillShapes, StickerIcon = _sticker },
        };
        AddLayer(shape);
        Select(shape);
        StartDrag(e, shape, TwoPointAnnotation.EndHandle, creating: true, p);
    }

    private void CreateText(TextDrag drag)
    {
        var text = new TextAnnotation(drag.Start, _color, _toolWeights[Tool.Text], Unit) { Boxed = _textBoxed, FontFamily = _font };
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

        AddLayer(text);
        Select(text);
        BeginTextEdit(text, isNew: true);
    }

    /// <summary>The handle under <paramref name="p"/>: an index into the handles, <see cref="RotateHandle"/>, or null.</summary>
    private int? HitHandle(Annotation annotation, Vector2 p)
    {
        float reach = (HandleRadius + HitTolerance) / _view.M11;
        var handles = annotation.Handles;
        for (int i = 0; i < handles.Count; i++)
        {
            if (Vector2.Distance(handles[i], p) <= reach)
                return i;
        }
        if (Vector2.Distance(RotateHandleAt(annotation).Handle, p) <= reach)
            return RotateHandle;
        return null;
    }

    private void StartDrag(PointerRoutedEventArgs e, Annotation target, int handle, bool creating, Vector2 p)
    {
        var pivot = MiddleOf(target);
        _drag = new DragState(e.Pointer.PointerId, target, handle, creating, p)
        {
            Aspect = DragAspect(target, creating),
            Pivot = pivot,
            StartAngle = MathF.Atan2(p.Y - pivot.Y, p.X - pivot.X),
            FromAngle = target.Angle,
            Movers = handle == MoveWhole && _group.Contains(target) ? _group.ToList() : [target],
        };
        CanvasHost.CapturePointer(e.Pointer);
        Canvas.Invalidate();
    }

    private static bool Shifted(PointerRoutedEventArgs e) => e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);

    /// <summary>
    /// The proportions a Shift-drag keeps: a new shape is drawn even (a square, a circle, a
    /// triangle with equal sides); an existing box keeps the proportions it has.
    /// </summary>
    private static float DragAspect(Annotation target, bool creating)
    {
        if (creating)
            return target is ShapeAnnotation { Kind: ShapeKind.Triangle } ? 2 / MathF.Sqrt(3) : 1;
        if (target is BoxAnnotation box && box.Shape is { Width: > 0, Height: > 0 } shape)
            return (float)(shape.Width / shape.Height);
        return 1;
    }

    /// <summary>
    /// Where a dragged handle goes with Shift held: a box's corner keeps its proportions
    /// (see <see cref="DragAspect"/>), and an end of an arrow or highlight snaps to a multiple of 45°.
    /// </summary>
    private static Vector2 Constrain(DragState drag, Vector2 p)
    {
        switch (drag.Target)
        {
            case BoxAnnotation box:
            {
                // In the box's own frame, so a turned box keeps its proportions along its own sides.
                var anchor = box.ToFrame(box.Handles[drag.Handle ^ 1]);
                var d = box.ToFrame(p) - anchor;
                float width = Math.Max(Math.Abs(d.X), Math.Abs(d.Y) * drag.Aspect);
                var size = new Vector2(width, width / drag.Aspect);
                return box.ToCanvas(anchor + new Vector2(d.X < 0 ? -size.X : size.X, d.Y < 0 ? -size.Y : size.Y));
            }
            case TwoPointAnnotation line:
                return SnapTo45(drag.Handle == 0 ? line.End : line.Start, p);
            default:
                return p;
        }
    }

    /// <summary><paramref name="p"/> moved onto the nearest line from <paramref name="anchor"/> at a multiple of 45°.</summary>
    private static Vector2 SnapTo45(Vector2 anchor, Vector2 p)
    {
        var d = p - anchor;
        float step = MathF.PI / 4;
        float angle = MathF.Round(MathF.Atan2(d.Y, d.X) / step) * step;
        var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        return anchor + direction * Vector2.Dot(d, direction);
    }

    /// <summary>Turns the dragged object to follow the pointer around its middle; Shift steps by <see cref="RotateStep"/>°.</summary>
    private static void Turn(DragState drag, Vector2 p, bool shift)
    {
        var target = drag.Target;
        float turned = MathF.Atan2(p.Y - drag.Pivot.Y, p.X - drag.Pivot.X) - drag.StartAngle;
        if (shift)
        {
            // Snap the angle it ends up at (for lines, which keep no angle, how far they turn).
            float step = RotateStep * MathF.PI / 180;
            turned = MathF.Round((drag.FromAngle + turned) / step) * step - drag.FromAngle;
        }
        target.RotateBy(turned - drag.Turned, drag.Pivot);
        drag.Turned = turned;
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

        if (_polygonDraft is not null)
        {
            _polygonCursor = Shifted(e) ? SnapTo45(_polygonDraft.Points[^1], p) : p;
            Canvas.Invalidate();
        }

        if (_cropDrag is not null && e.Pointer.PointerId == _cropDrag.PointerId)
        {
            CropMoved(p);
            e.Handled = true;
            return;
        }

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

        bool shift = Shifted(e);
        if (_drag.Handle == RotateHandle)
        {
            Turn(_drag, p, shift);
        }
        else if (_drag.Handle >= 0)
        {
            // Pictures keep their proportions unless Shift is held; shapes keep them only with Shift.
            bool keep = shift != (_drag.Target is ImageAnnotation);
            _drag.Target.MoveHandle(_drag.Handle, keep ? Constrain(_drag, p) : p);
        }
        else
        {
            // Shift keeps a move straight across or straight up and down.
            var moved = p - _drag.Start;
            if (shift)
                moved = Math.Abs(moved.X) >= Math.Abs(moved.Y) ? new Vector2(moved.X, 0) : new Vector2(0, moved.Y);
            foreach (var mover in _drag.Movers)
                mover.MoveBy(moved - _drag.Moved);
            _drag.Moved = moved;
        }
        _drag.Changed |= p != _drag.Start;

        Canvas.Invalidate();
        e.Handled = true;
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pan is { } pan && e.Pointer.PointerId == pan.PointerId)
        {
            _pan = null;
            CanvasHost.ReleasePointerCapture(e.Pointer);
            CanvasHost.Cursor = _spaceHeld ? PanCursor : null;
            UpdateCursor(null);
            e.Handled = true;
            return;
        }

        if (_cropDrag is not null && e.Pointer.PointerId == _cropDrag.PointerId)
        {
            CropReleased(e);
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

        // A sticker placed with a click (no drag) gets the size from the size setting.
        if (_drag.Creating && _drag.Target is ShapeAnnotation { Kind: ShapeKind.Sticker } sticker &&
            Math.Abs(sticker.End.X - sticker.Start.X) < MinShapeSize && Math.Abs(sticker.End.Y - sticker.Start.Y) < MinShapeSize)
        {
            var half = new Vector2(StickerSide(sticker.Weight) / 2);
            sticker.Start -= half;
            sticker.End = sticker.Start + half * 2;
        }

        // A click without a drag shouldn't leave an invisible zero-size shape behind.
        if (_drag.Creating && _drag.Target is TwoPointAnnotation shape &&
            Math.Abs(shape.End.X - shape.Start.X) < MinShapeSize && Math.Abs(shape.End.Y - shape.Start.Y) < MinShapeSize)
        {
            _document.Annotations.Remove(shape);
            Select(null);
        }
        else if (_drag.Changed || _drag.Creating)
        {
            Commit();
        }

        _drag = null;
        CanvasHost.ReleasePointerCapture(e.Pointer);
        SyncSlider(); // resizing text changes its size
        Canvas.Invalidate();
        e.Handled = true;
    }

    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_polygonDraft is not null)
        {
            FinishPolygon();
            e.Handled = true;
            return;
        }
        var p = ToDocument(e.GetPosition(Canvas).ToVector2());
        if (_selected is { Angle: not 0 } turned && HitHandle(turned, p) == RotateHandle)
        {
            // Double-clicking the rotate handle straightens it.
            turned.RotateBy(-turned.Angle, MiddleOf(turned));
            Commit();
            Canvas.Invalidate();
            e.Handled = true;
            return;
        }
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

    // Space, and Enter or Esc while cropping, are caught on the way down (preview), so they
    // act on the canvas instead of clicking whichever toolbar button has focus.
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_editingText is not null || e.OriginalSource is TextBox)
            return;
        if (_polygonDraft is not null && e.Key is VirtualKey.Enter or VirtualKey.Escape)
        {
            // Either finishes the polygon being clicked out.
            FinishPolygon();
            e.Handled = true;
            return;
        }
        if (_tool == Tool.Crop && e.Key is VirtualKey.Enter or VirtualKey.Escape)
        {
            // Enter applies the crop (by leaving the tool); Esc puts it back as it was.
            if (e.Key == VirtualKey.Escape)
                CancelCrop();
            SetTool(Tool.Select);
            e.Handled = true;
            return;
        }
        if (e.Key != VirtualKey.Space)
            return;
        if (!_spaceHeld)
        {
            _spaceHeld = true;
            if (_pan is null)
                CanvasHost.Cursor = PanCursor;
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
    private readonly InputCursor _textCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private readonly InputCursor _moveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private readonly InputCursor _resizeNwSeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
    private readonly InputCursor _resizeNeSwCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);
    private readonly InputCursor _resizeNsCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    private readonly InputCursor _resizeWeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private InputCursor? _openHandCursor, _grabHandCursor, _rotateCursor, _crossCursor;

    // Windshot's own cursors (see Shell.AppCursors) are made on first use, when the display
    // scale is known; each falls back to the nearest system cursor.

    /// <summary>Ready to pan (Space held): an open hand.</summary>
    private InputCursor PanCursor => _openHandCursor ??= Shell.AppCursors.Open(RasterScale) ?? _moveCursor;

    /// <summary>Panning: a grabbing hand.</summary>
    private InputCursor PanningCursor => _grabHandCursor ??= Shell.AppCursors.Grab(RasterScale) ?? _moveCursor;

    /// <summary>Over the rotate handle, and while turning: a clockwise arrow.</summary>
    private InputCursor RotateCursor => _rotateCursor ??= Shell.AppCursors.Rotate(RasterScale) ?? InputSystemCursor.Create(InputSystemCursorShape.Hand);

    /// <summary>Drawing tools: a thin crosshair.</summary>
    private InputCursor CrossCursor => _crossCursor ??= Shell.AppCursors.Cross(RasterScale) ?? InputSystemCursor.Create(InputSystemCursorShape.Cross);

    /// <summary>Blur and spotlight can only be grabbed with Select or their own tool.</summary>
    private bool GrabsAreaEffects => _tool is Tool.Select or Tool.Blur or Tool.Spotlight;

    /// <summary>
    /// Crosshair for drawing tools, I-beam for text, arrow for select; over something that
    /// can be grabbed, a move cursor; over a handle, a resize arrow pointing the way it drags.
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
            _ => CrossCursor,
        };

        if (point is Vector2 p)
        {
            if (_tool == Tool.Crop)
                cursor = CropCursor(p);
            else if (_selected is not null && HitHandle(_selected, p) is int handle)
                cursor = HandleCursor(_selected, handle);
            else if (GrabAt(p, HitTolerance / _view.M11) is not null)
                cursor = _moveCursor;
        }
        CanvasHost.Cursor = cursor;
    }

    /// <summary>
    /// Corners of boxes and text resize diagonally, so they get the resize arrow pointing that
    /// way (turned with the object); the ends of arrows and highlights go anywhere, so they get
    /// the move cursor; the rotate handle gets a hand.
    /// </summary>
    private InputCursor HandleCursor(Annotation annotation, int handle)
    {
        if (handle == RotateHandle)
            return RotateCursor;
        var handles = annotation.Handles;
        if (handles.Count != 4)
            return _moveCursor;
        // The corner's diagonal in the object's frame, turned onto the screen.
        var center = annotation.ToFrame((handles[0] + handles[1] + handles[2] + handles[3]) / 4);
        var offset = annotation.ToFrame(handles[handle]) - center;
        var diagonal = Vector2.TransformNormal(new Vector2(Math.Sign(offset.X), Math.Sign(offset.Y)), Matrix3x2.CreateRotation(annotation.Angle));
        // Nearest of the four resize arrows, by 45° steps from horizontal (y points down).
        int step = (int)MathF.Round(MathF.Atan2(diagonal.Y, diagonal.X) / (MathF.PI / 4));
        return (((step % 4) + 4) % 4) switch
        {
            0 => _resizeWeCursor,
            1 => _resizeNwSeCursor,
            2 => _resizeNsCursor,
            _ => _resizeNeSwCursor,
        };
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
            FontFamily = new FontFamily(text.FontFamily),
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

        // Turned text is typed turned: the box turns about its top-left, where the text starts.
        var position = Vector2.Transform(text.ToCanvas(text.Position), _view);
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(box, position.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(box, position.Y);
        if (text.Angle != 0)
            box.RenderTransform = new RotateTransform { Angle = text.Angle * 180 / Math.PI };

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

    /// <summary>Finishes whatever is in progress (typing, cropping) so exports include it.</summary>
    private void FinishEditing()
    {
        CommitTextEdit();
        if (_tool == Tool.Crop)
            SetTool(Tool.Select);
    }

    // Export handlers are async void, so an exception escaping one would take down the whole
    // app, every other open editor included. Each catches its own and says what went wrong.

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FinishEditing();
            var png = await _document.EncodePngAsync();
            if (!Shell.ClipboardWriter.TrySetPng(png))
            {
                ShowMessage("Couldn't copy: the clipboard is busy. Try again.");
                return;
            }
            CloseAfterExport("Copied");
        }
        catch (Exception ex)
        {
            Log.Write($"Copy failed: {ex}");
            ShowMessage("Couldn't copy the image");
        }
    }

    /// <summary>A short message centered on this window.</summary>
    private void ShowMessage(string message)
    {
        var window = new System.Drawing.Rectangle(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        Shell.Hud.Show(message, window, Content.XamlRoot.RasterizationScale);
    }

    /// <summary>After a copy or save, close the editor (unless turned off in settings), saying what happened.</summary>
    private void CloseAfterExport(string message)
    {
        if (!Settings.Current.Editor.CloseAfterSaveOrCopy)
            return;
        ShowMessage(message);
        Close();
    }

    /// <summary>Reads the text in the original screenshot (annotations ignored) and copies it.</summary>
    private async void CopyText_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FinishEditing();
            var size = _document.Image.SizeInPixels;
            // Reports its own failures as the message.
            ShowMessage(await TextRecognizer.CopyToClipboardAsync(
                _document.Image.GetPixelBytes(), (int)size.Width, (int)size.Height, _document.SourceScale));
        }
        catch (Exception ex)
        {
            Log.Write($"Copy text failed: {ex}");
            ShowMessage("Couldn't read text");
        }
    }

    /// <summary>
    /// Floats the finished image above other windows, exactly where it was captured, and
    /// closes the editor.
    /// </summary>
    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FinishEditing();
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
        catch (Exception ex)
        {
            Log.Write($"Pin failed: {ex}");
            ShowMessage("Couldn't pin the image");
        }
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
        [nameof(Tool.Rectangle)] = "shapes",
        [nameof(Tool.Text)] = "text_t",
        [nameof(Tool.Blur)] = "blur",
        [nameof(Tool.Step)] = "number_circle_1",
        [nameof(Tool.Highlighter)] = "highlight",
        [nameof(Tool.Spotlight)] = "flashlight",
        [nameof(Tool.Crop)] = "crop",
        [nameof(Tool.Image)] = "image",
    };

    /// <summary>
    /// Gives every toolbar button its Fluent icon. Like Windows 11's own toolbars, icons are
    /// outlined normally and filled for the active tool or toggle, and while hovered.
    /// </summary>
    private void InitializeIcons()
    {
        BindIcon(SaveButton, "save");
        ToolTipService.SetToolTip(SaveButton, Settings.Current.Saving.SaveWithoutAsking
            ? $"Save to {Path.GetFileName(Settings.Current.Saving.EffectiveFolder.TrimEnd(Path.DirectorySeparatorChar))} (Ctrl+S). Ctrl+Shift+S to save as."
            : "Save (Ctrl+S)");
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
        BindIcon(InsertImageButton, "image_add");
        BindIcon(LayersButton, "layer");
        var beautify = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        beautify.Children.Add(Shell.FluentIcons.Create("sparkle", filled: false));
        beautify.Children.Add(_beautifyLabel);
        BeautifyButton.Content = beautify;
    }

    /// <summary>
    /// The compact button width (from the toolbar's style, or set on the shape buttons) would
    /// clip labels in the "…" menu, so buttons there size to the menu instead, and get it back
    /// when they return.
    /// </summary>
    private void Toolbar_DynamicOverflowItemsChanging(CommandBar sender, DynamicOverflowItemsChangingEventArgs args)
    {
        // Raised before the items move; IsInOverflow is up to date once this layout pass is done.
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var element in sender.PrimaryCommands.OfType<Control>())
            {
                bool inMenu = ((ICommandBarElement)element).IsInOverflow;
                if (element is AppBarButton or AppBarToggleButton)
                {
                    if (inMenu)
                        element.Width = double.NaN;
                    else if (element is AppBarToggleButton toggle && ShapeButtons.Contains(toggle))
                        element.Width = IconButtonWidth; // set on the button itself, not by a style
                    else
                        element.ClearValue(FrameworkElement.WidthProperty);
                }
                else if (element is AppBarElementContainer)
                {
                    // Line the controls up with the menu items' icons.
                    // (The Size label has no button padding of its own, so it needs a little more.)
                    double indent = element == SizeContainer ? OverflowIndent + 4 : OverflowIndent;
                    element.Padding = new Thickness(inMenu ? indent : 0, 0, 0, 0);
                }
            }
            // The Beautify button is a bare icon on the bar; in the menu it needs words.
            _beautifyLabel.Visibility = BeautifyContainer.IsInOverflow ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private const double OverflowIndent = 24;

    private readonly TextBlock _beautifyLabel = new() { Text = "Beautify", FontSize = 14, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };

    // The icon is swapped only when it changes: PointerEntered bubbles up from the icon itself,
    // so swapping on every one swaps the icon out from under the pointer again and again, and a
    // click pressed on an icon that's gone by the release doesn't count.
    private void BindIcon(AppBarButton button, string icon)
    {
        bool? shown = null;
        void Show(bool filled)
        {
            if (shown == filled)
                return;
            shown = filled;
            button.Icon = Shell.FluentIcons.Create(icon, filled);
        }
        button.PointerEntered += (_, _) => Show(button.IsEnabled);
        button.PointerExited += (_, _) => Show(false);
        Show(false);
    }

    private void BindIcon(AppBarToggleButton button, string icon)
    {
        bool hovered = false;
        bool? shown = null;
        void Refresh()
        {
            bool filled = hovered || button.IsChecked == true;
            if (shown == filled)
                return;
            shown = filled;
            button.Icon = Shell.FluentIcons.Create(icon, filled);
        }
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

    private void Save_Click(object sender, RoutedEventArgs e) => Save(ask: !Settings.Current.Saving.SaveWithoutAsking);

    private void Save(bool ask)
    {
        if (ask)
            SaveAs();
        else
            SaveToFolder();
    }

    /// <summary>Saves straight to the folder from settings (Pictures\Screenshots unless changed), under a new name.</summary>
    private async void SaveToFolder()
    {
        string folder = Settings.Current.Saving.EffectiveFolder;
        try
        {
            FinishEditing();
            Directory.CreateDirectory(folder);
            string path = UniquePath(folder, $"Windshot {DateTime.Now:yyyy-MM-dd HHmmss}");
            using var png = await _document.EncodePngAsync();
            using (var input = png.AsStreamForRead())
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                await input.CopyToAsync(output);
            Log.Write($"Saved {path}");
            string message = $"Saved to {Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))}";
            // No dialog was shown, so say so even when the editor stays open.
            if (Settings.Current.Editor.CloseAfterSaveOrCopy)
                CloseAfterExport(message);
            else
                ShowMessage(message);
        }
        catch (Exception ex)
        {
            // E.g. the folder is on a drive that's gone, read-only, or full.
            Log.Write($"Save to {folder} failed: {ex}");
            ShowMessage($"Couldn't save to {folder}. Pick another folder in Settings, or use Save as (Ctrl+Shift+S).");
        }
    }

    /// <summary><paramref name="name"/>.png in the folder, or with " 2", " 3"… added if that's taken (two saves in one second).</summary>
    private static string UniquePath(string folder, string name)
    {
        string path = Path.Combine(folder, name + ".png");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{name} {n}.png");
        return path;
    }

    /// <summary>Asks where to save.</summary>
    private async void SaveAs()
    {
        Windows.Storage.StorageFile? file = null;
        try
        {
            FinishEditing();
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = $"Windshot {DateTime.Now:yyyy-MM-dd HHmmss}",
            };
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            using var png = await _document.EncodePngAsync();
            using (var output = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
            {
                output.Size = 0;
                await RandomAccessStream.CopyAndCloseAsync(png.GetInputStreamAt(0), output.GetOutputStreamAt(0));
            }
            CloseAfterExport($"Saved {file.Name}");
        }
        catch (Exception ex)
        {
            // E.g. the file is open in another app, the drive is full or read-only.
            Log.Write($"Save failed: {ex}");
            ShowMessage(file is null ? "Couldn't save" : $"Couldn't save {file.Name}");
        }
    }
}
