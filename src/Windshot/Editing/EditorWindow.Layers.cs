using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Windshot.Editing;

/// <summary>
/// The layers panel (L, or the toolbar's Layers button): every object topmost first, with the
/// screenshot pinned at the bottom. Click to select, drag to restack, the eye to hide,
/// double-click to rename; the slider sets the selected layer's opacity. Ctrl+] and Ctrl+[
/// restack the selection from the keyboard (with Shift, to the top or bottom).
/// </summary>
public sealed partial class EditorWindow
{

    /// <summary>
    /// The rows, topmost first. They live directly in the ListView's own item collection (which
    /// supports drag-reordering too) rather than in a bound .NET collection: in the trimmed
    /// Release build, handing WinUI an ObservableCollection fails for lack of the reflection
    /// its interop glue needs.
    /// </summary>
    private IEnumerable<Grid> LayerRows => LayerList.Items.Cast<Grid>();
    /// <summary>Set while the panel is being updated from the document, so its events don't echo back.</summary>
    private bool _syncingLayers;
    /// <summary>The screenshot row is selected (for its opacity) rather than an object.</summary>
    private bool _imageLayerSelected;
    private bool _layersOpen;

    private void InitializeLayers()
    {
        LayerList.SelectionChanged += (_, _) =>
        {
            if (_syncingLayers || (LayerList.SelectedItem as FrameworkElement)?.Tag is not Annotation annotation)
                return;
            if (_tool == Tool.Crop)
                SetTool(Tool.Select);
            CommitTextEdit();
            Select(annotation);
        };
        // Dragging rows restacks the objects (the list is topmost first).
        LayerList.DragItemsCompleted += (_, _) => ApplyLayerOrder();

        ImageLayerIcon.Content = Shell.FluentIcons.Create("image", filled: false);
        ImageLayerRow.Tapped += (_, _) => SelectImageLayer();
        ImageLayerEye.Click += (_, _) =>
        {
            _document.ImageVisible = !_document.ImageVisible;
            Commit();
            Canvas.Invalidate();
        };
        LayerOpacity.ValueChanged += LayerOpacity_ValueChanged;
        DeleteLayerButton.Content = Shell.FluentIcons.Create("delete", filled: false);
        DeleteLayerButton.Click += (_, _) => DeleteSelected();
        SetLayersOpen(Settings.Current.Editor.ShowLayers);
    }

    private void LayersButton_Click(object sender, RoutedEventArgs e) => SetLayersOpen(LayersButton.IsChecked == true);

    private void SetLayersOpen(bool open)
    {
        _layersOpen = open;
        LayersButton.IsChecked = open;
        LayersPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ApplyMinimumSize();
        RefreshLayers();
    }

    /// <summary>
    /// Adds a new object to the stack: on top, except effects, which go just above the
    /// screenshot (and any effects already there), so a new blur or highlight marks the
    /// screenshot rather than the annotations. Drag it higher to make it cover them too.
    /// </summary>
    private void AddLayer(Annotation annotation)
    {
        var list = _document.Annotations;
        if (!annotation.IsEffect)
        {
            list.Add(annotation);
            return;
        }
        int index = 0;
        while (index < list.Count && list[index].IsEffect)
            index++;
        list.Insert(index, annotation);
    }

    private void DeleteSelected()
    {
        if (_selected is not { } annotation)
            return;
        CommitTextEdit();
        _document.Annotations.Remove(annotation);
        (annotation as IDisposable)?.Dispose();
        Select(null);
        Commit();
    }

    /// <summary>Moves the selection up (+1) or down (-1) the stack, or all the way.</summary>
    private void MoveLayer(int direction, bool allTheWay)
    {
        if (_selected is not { } annotation)
            return;
        var list = _document.Annotations;
        int from = list.IndexOf(annotation);
        int to = allTheWay ? (direction > 0 ? list.Count - 1 : 0) : Math.Clamp(from + direction, 0, list.Count - 1);
        if (from < 0 || from == to)
            return;
        list.RemoveAt(from);
        list.Insert(to, annotation);
        Commit();
        Canvas.Invalidate();
    }

    private void ApplyLayerOrder()
    {
        var order = LayerRows.Select(row => (Annotation)row.Tag).Reverse().ToList();
        if (order.SequenceEqual(_document.Annotations))
            return;
        _document.Annotations.Clear();
        _document.Annotations.AddRange(order);
        Commit();
        Canvas.Invalidate();
    }

    private void SelectImageLayer()
    {
        if (_tool == Tool.Crop)
            SetTool(Tool.Select);
        CommitTextEdit();
        Select(null);
        _imageLayerSelected = true;
        SyncLayerSelection();
    }

    private void LayerOpacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingLayers)
            return;
        float opacity = (float)(e.NewValue / 100);
        if (_selected is { } annotation)
        {
            annotation.Opacity = opacity;
            Commit(coalesceKey: ("opacity", annotation));
        }
        else if (_imageLayerSelected)
        {
            _document.ImageOpacity = opacity;
            Commit(coalesceKey: ("opacity", _document));
        }
        Canvas.Invalidate();
    }

    /// <summary>Brings the panel up to date with the document (after any change, undo or redo).</summary>
    private void RefreshLayers()
    {
        if (!_layersOpen)
            return;
        _document.NumberSteps();
        _syncingLayers = true;
        var order = _document.Annotations.AsEnumerable().Reverse().ToList();
        if (order.SequenceEqual(LayerRows.Select(row => (Annotation)row.Tag)))
        {
            foreach (var row in LayerRows)
                FillRow(row, (Annotation)row.Tag);
        }
        else
        {
            LayerList.Items.Clear();
            foreach (var annotation in order)
            {
                var row = new Grid { Tag = annotation, Height = 40, ColumnSpacing = 10, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.DoubleTapped += (_, e) =>
                {
                    BeginRename(row);
                    e.Handled = true;
                };
                FillRow(row, annotation);
                LayerList.Items.Add(row);
            }
        }

        ImageLayerEye.Content = Shell.FluentIcons.Create(_document.ImageVisible ? "eye" : "eye_off", filled: false);
        ImageLayerName.Opacity = _document.ImageVisible ? 1 : 0.5;
        _syncingLayers = false;
        SyncLayerSelection();
    }

    /// <summary>(Re)draws a row: the tool's icon with a dot of the object's color, its name, and the eye.</summary>
    private void FillRow(Grid row, Annotation annotation)
    {
        row.Children.Clear();

        var icon = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(Shell.FluentIcons.Create(LayerIcon(annotation), filled: false));
        if (!annotation.IsEffect || annotation is HighlighterAnnotation)
        {
            icon.Children.Add(new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(annotation.Color),
                Stroke = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -3, -3),
            });
        }
        row.Children.Add(icon);

        var label = new TextBlock
        {
            Text = LayerName(annotation),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = annotation.Visible ? 1 : 0.5,
        };
        if (annotation.Name is not null)
            label.FontWeight = FontWeights.SemiBold; // named by hand
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        var eye = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = Shell.FluentIcons.Create(annotation.Visible ? "eye" : "eye_off", filled: false),
        };
        ToolTipService.SetToolTip(eye, annotation.Visible ? "Hide" : "Show");
        eye.Click += (_, _) =>
        {
            annotation.Visible = !annotation.Visible;
            Commit();
            Canvas.Invalidate();
        };
        Grid.SetColumn(eye, 2);
        row.Children.Add(eye);
    }

    private static string LayerName(Annotation annotation) => annotation.Name ?? annotation switch
    {
        ArrowAnnotation => "Arrow",
        TextAnnotation text when !string.IsNullOrWhiteSpace(text.Text) => $"“{text.Text.Trim().Split('\n')[0].Trim()}”",
        TextAnnotation => "Text",
        BlurAnnotation blur => blur.Pixelate ? "Pixelate" : "Blur",
        StepAnnotation step => $"Step {step.Number}",
        SpotlightAnnotation => "Spotlight",
        HighlighterAnnotation => "Highlight",
        ShapeAnnotation { Kind: ShapeKind.Sticker } sticker => Stickers.NameOf(sticker.StickerIcon),
        ShapeAnnotation shape => shape.Kind.ToString(),
        PolygonAnnotation => "Polygon",
        _ => "Shape",
    };

    /// <summary>A layer's icon: its tool's, or for shapes, its own shape.</summary>
    private static string LayerIcon(Annotation annotation) => annotation switch
    {
        ShapeAnnotation { Kind: ShapeKind.Sticker } sticker => sticker.StickerIcon,
        ShapeAnnotation shape => ShapeIcon(shape.Kind),
        PolygonAnnotation => ShapeIcon(ShapeKind.Polygon),
        _ => ToolIcons[ToolFor(annotation).ToString()],
    };

    /// <summary>Swaps a row's name for a text box. Enter or clicking away keeps it, Esc cancels, and an empty name goes back to the default.</summary>
    private void BeginRename(Grid row)
    {
        if (row.Tag is not Annotation annotation || row.Children.OfType<TextBlock>().FirstOrDefault() is not { } label)
            return;
        var box = new TextBox { Text = LayerName(annotation), VerticalAlignment = VerticalAlignment.Center, MinHeight = 0, Padding = new Thickness(6, 2, 6, 2) };
        Grid.SetColumn(box, 1);
        row.Children.Remove(label);
        row.Children.Add(box);

        bool done = false;
        void Finish(bool keep)
        {
            if (done)
                return;
            done = true;
            string name = box.Text.Trim();
            if (keep)
            {
                annotation.Name = name.Length == 0 ? null : name;
                annotation.Name = annotation.Name == LayerName(WithoutName(annotation)) ? null : annotation.Name;
                Commit();
            }
            FillRow(row, annotation);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Escape)
            {
                Finish(keep: e.Key == VirtualKey.Enter);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Finish(keep: true);
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
    }

    /// <summary>A copy without a custom name, to compare against its default name.</summary>
    private static Annotation WithoutName(Annotation annotation)
    {
        var copy = annotation.Clone();
        copy.Name = null;
        return copy;
    }

    /// <summary>Shows the canvas selection in the panel, and the selected layer's opacity on the slider.</summary>
    private void SyncLayerSelection()
    {
        if (_selected is not null)
            _imageLayerSelected = false;
        if (!_layersOpen)
            return;
        _syncingLayers = true;
        LayerList.SelectedItem = LayerRows.FirstOrDefault(row => row.Tag == _selected);
        ImageLayerRow.Background = _imageLayerSelected
            ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        float? opacity = _selected?.Opacity ?? (_imageLayerSelected ? _document.ImageOpacity : null);
        LayerOpacity.IsEnabled = opacity is not null;
        LayerOpacity.Value = Math.Round((opacity ?? 1) * 100);
        DeleteLayerButton.IsEnabled = _selected is not null;
        _syncingLayers = false;
    }
}
