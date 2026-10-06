using Microsoft.UI.Xaml;

namespace Windshot.Editing;

/// <summary>
/// The Style menu on the options bar: outline, shadow and corner rounding (see
/// <see cref="LayerStyle"/>) for the selected layers or, with nothing selected, for what the
/// current tool draws next. Each tool remembers its style, like its size.
/// </summary>
public sealed partial class EditorWindow
{
    private Dictionary<Tool, LayerStyle> _toolStyles = new();
    /// <summary>Set while the menu shows a style, so its change events don't apply it back.</summary>
    private bool _syncingLayerStyle;

    /// <summary>The tools whose layers take a style: everything that draws, not the effects.</summary>
    private static bool Styleable(Tool tool) => tool is Tool.Arrow or Tool.Rectangle or Tool.Text or Tool.Step or Tool.Image;

    private void InitializeLayerStyle()
    {
        _toolStyles = new Dictionary<Tool, LayerStyle>(Settings.Current.Editor.Styles);
        StyleIcon.Content = Shell.FluentIcons.Create("square_shadow", filled: false);
        OutlineSlider.ValueChanged += (_, e) => ChangeStyle(s => s with { Outline = (int)e.NewValue });
        ShadowSlider.ValueChanged += (_, e) => ChangeStyle(s => s with { Shadow = (int)e.NewValue });
        CornersSlider.ValueChanged += (_, e) => ChangeStyle(s => s with { Corners = (int)e.NewValue });
        LightOutline.Checked += (_, _) => ChangeStyle(s => s with { DarkOutline = false });
        DarkOutline.Checked += (_, _) => ChangeStyle(s => s with { DarkOutline = true });

        // The same controls laid out on the bar, for pictures (see SyncOptions).
        InlineOutlineColor.Items.Add("White");
        InlineOutlineColor.Items.Add("Black");
        InlineOutlineSlider.ValueChanged += (_, e) => ChangeStyle(s => s with { Outline = (int)e.NewValue });
        InlineShadowSlider.ValueChanged += (_, e) => ChangeStyle(s => s with { Shadow = (int)e.NewValue });
        InlineCornersSlider.ValueChanged += (_, e) => ChangeStyle(s => s with { Corners = (int)e.NewValue });
        InlineOutlineColor.SelectionChanged += (_, _) =>
        {
            if (InlineOutlineColor.SelectedIndex >= 0)
                ChangeStyle(s => s with { DarkOutline = InlineOutlineColor.SelectedIndex == 1 });
        };
    }

    /// <summary>
    /// Pictures have nothing else on the options bar, so their style is laid out on it rather
    /// than in the Style menu: for a selected picture, or a group of nothing but pictures.
    /// </summary>
    private void ShowStyle(bool styleable, bool onlyPictures)
    {
        Show(StyleContainer, styleable && !onlyPictures);
        Show(InlineStyleContainer, styleable && onlyPictures);
    }

    /// <summary>The selected layers that take a style.</summary>
    private List<Annotation> StyleTargets => Selection.Where(a => a.CanStyle).ToList();

    private LayerStyle ToolStyle(Tool tool) => _toolStyles.GetValueOrDefault(tool) ?? LayerStyle.None;

    private void ChangeStyle(Func<LayerStyle, LayerStyle> change)
    {
        if (_syncingLayerStyle)
            return;
        CommitTextEdit();
        var targets = StyleTargets;
        if (targets.Count > 0)
        {
            foreach (var layer in targets)
                layer.Style = change(layer.Style);
            // The next one drawn matches, as with size.
            _toolStyles[ToolFor(targets[^1])] = targets[^1].Style;
            Commit(coalesceKey: ("style", _group.Count > 0 ? _groupKey : _selected));
        }
        else if (Styleable(_tool))
        {
            _toolStyles[_tool] = change(ToolStyle(_tool));
        }
        SyncLayerStyle();
        Canvas.Invalidate();
    }

    /// <summary>
    /// Shows the style of the selection (the topmost layer's, for a group) or of the current
    /// tool, with the corners slider only for layers that have corners to round.
    /// </summary>
    private void SyncLayerStyle()
    {
        var targets = StyleTargets;
        var style = targets.Count > 0 ? targets[^1].Style : ToolStyle(_tool);
        bool corners = targets.Count > 0
            ? targets.Any(a => a.HasCorners)
            : _tool switch
            {
                Tool.Rectangle => _shapeKind == ShapeKind.Rectangle,
                Tool.Text => _textBoxed,
                _ => false,
            };
        _syncingLayerStyle = true;
        OutlineSlider.Value = style.Outline;
        ShadowSlider.Value = style.Shadow;
        CornersSlider.Value = style.Corners;
        LightOutline.IsChecked = !style.DarkOutline;
        DarkOutline.IsChecked = style.DarkOutline;
        CornersSlider.Visibility = corners ? Visibility.Visible : Visibility.Collapsed;
        InlineOutlineSlider.Value = style.Outline;
        InlineShadowSlider.Value = style.Shadow;
        InlineCornersSlider.Value = style.Corners;
        InlineOutlineColor.SelectedIndex = style.DarkOutline ? 1 : 0;
        _syncingLayerStyle = false;
    }
}
