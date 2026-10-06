using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Windshot.Editing;

/// <summary>Text fonts, and colors beyond the color set (the color wheel under "More colors…").</summary>
public sealed partial class EditorWindow
{
    /// <summary>Set while showing a value in a picker, so its change event doesn't apply it back.</summary>
    private bool _syncingStyle;

    // ---- Options bar -------------------------------------------------------------------

    /// <summary>
    /// Shows what the options bar has to offer for the selected layer or, with nothing selected,
    /// for the current tool; with nothing to set (selecting or cropping), a hint instead.
    /// </summary>
    private void SyncOptions()
    {
        if (_group.Count > 0)
        {
            SyncGroupOptions();
            return;
        }
        var subject = _selected is { } selected ? ToolFor(selected) : _tool;
        bool sized = _toolWeights.ContainsKey(subject);
        bool colored = subject is Tool.Arrow or Tool.Rectangle or Tool.Text or Tool.Step or Tool.Highlighter;
        Show(SizeContainer, sized);
        Show(ColorContainer, colored);
        Show(BlendContainer, subject == Tool.Highlighter);
        Show(FontContainer, subject == Tool.Text);
        Show(TextBackgroundButton, subject == Tool.Text);
        Show(PixelateButton, subject == Tool.Blur);
        foreach (var button in ShapeButtons)
            Show(button, subject == Tool.Rectangle);
        Show(ShapeSeparator, subject == Tool.Rectangle);
        Show(FillButton, subject == Tool.Rectangle);

        OptionsHint.Text = subject == Tool.Crop
            ? "Drag the edges or draw a new area. Enter applies, Esc cancels."
            : "Click a layer to change it, or pick a tool to draw.";
        Show(HintContainer, !sized && !colored);
    }

    /// <summary>For layers selected together: their color, if any of them has one, and what else works on them.</summary>
    private void SyncGroupOptions()
    {
        bool colored = _group.Any(a => !a.IsAreaEffect);
        foreach (var element in new UIElement[] { SizeContainer, BlendContainer, FontContainer, TextBackgroundButton, PixelateButton, ShapeSeparator, FillButton })
            Show(element, false);
        foreach (var button in ShapeButtons)
            Show(button, false);
        Show(ColorContainer, colored);
        OptionsHint.Text = $"{_group.Count} layers selected. Drag one to move them all, or press Delete to remove them.";
        Show(HintContainer, true);
    }

    private static void Show(UIElement element, bool show) => element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    // ---- Fonts -------------------------------------------------------------------------

    private void InitializeFonts()
    {
        foreach (var (name, family) in TextAnnotation.Fonts)
            FontPicker.Items.Add(FontItem(name, family));
        FontPicker.SelectionChanged += (_, _) =>
        {
            if (_syncingStyle || FontPicker.SelectedItem is not ComboBoxItem { Tag: string family })
                return;
            if (Supporter.IsUnlocked || family == TextAnnotation.DefaultFontFamily)
            {
                SetFont(family);
                return;
            }
            // Fonts are a supporter extra: put the list back, and offer to unlock them.
            DispatcherQueue.TryEnqueue(async () =>
            {
                SyncFont();
                if (await RequireSupporter("Fonts are"))
                {
                    SetFont(family);
                    SyncFont();
                }
            });
        };
    }

    /// <summary>A font in the list, shown in its own typeface. (Items, not binding: see InitializeColorSets.)</summary>
    private static ComboBoxItem FontItem(string name, string family) =>
        new() { Content = name, FontFamily = new FontFamily(family), Tag = family };

    /// <summary>The font picker (on the options bar for text).</summary>
    private void SyncFont()
    {
        var text = _selected as TextAnnotation;
        string family = text?.FontFamily ?? _font;
        var item = FontPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == family);
        if (item is null)
        {
            // A font typed into settings.json: list it too, so it shows as the current one.
            item = FontItem(family, family);
            FontPicker.Items.Add(item);
        }
        _syncingStyle = true;
        FontPicker.SelectedItem = item;
        _syncingStyle = false;
    }

    private void SetFont(string family)
    {
        CommitTextEdit();
        _font = family;
        if (_selected is TextAnnotation text && text.FontFamily != family)
        {
            text.FontFamily = family;
            Commit();
        }
        Canvas.Invalidate();
    }

    // ---- Highlighter blend -------------------------------------------------------------

    private static readonly (HighlightBlend Blend, string Name, string Tip)[] Blends =
    [
        (HighlightBlend.Multiply, "Multiply", "Like real highlighter ink: text under it stays dark"),
        (HighlightBlend.Darken, "Darken", "Black text stays black; lighter text takes the ink's color"),
        (HighlightBlend.Overlay, "Overlay", "Tints and adds contrast"),
        (HighlightBlend.Screen, "Screen", "For dark backgrounds: lightens what's under it"),
    ];

    private void InitializeBlend()
    {
        foreach (var (_, name, tip) in Blends)
        {
            var item = new ComboBoxItem { Content = name };
            ToolTipService.SetToolTip(item, tip);
            BlendPicker.Items.Add(item);
        }
        BlendPicker.SelectionChanged += (_, _) =>
        {
            if (!_syncingStyle && BlendPicker.SelectedIndex >= 0)
                SetBlend(Blends[BlendPicker.SelectedIndex].Blend);
        };
    }

    /// <summary>The blend picker (on the options bar for highlights).</summary>
    private void SyncBlend()
    {
        var highlight = _selected as HighlighterAnnotation;
        _syncingStyle = true;
        BlendPicker.SelectedIndex = Array.FindIndex(Blends, b => b.Blend == (highlight?.Blend ?? _highlightBlend));
        _syncingStyle = false;
    }

    private void SetBlend(HighlightBlend blend)
    {
        _highlightBlend = blend;
        if (_selected is HighlighterAnnotation highlight && highlight.Blend != blend)
        {
            highlight.Blend = blend;
            Commit();
        }
        Canvas.Invalidate();
    }

    // ---- More colors -------------------------------------------------------------------

    private void InitializeMoreColors()
    {
        MoreColorsButton.Click += async (_, _) =>
        {
            bool show = MoreColorsButton.IsChecked == true;
            if (show && !Supporter.IsUnlocked)
            {
                // The color wheel is a supporter extra; once unlocked, it's there when the menu reopens.
                MoreColorsButton.IsChecked = false;
                ColorFlyout.Hide();
                await RequireSupporter("The color wheel is");
                return;
            }
            if (show)
                ShowColorInWheel();
            MoreColors.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        };
        // Starts where the current color is, e.g. after picking a swatch or another object.
        ColorFlyout.Opening += (_, _) => ShowColorInWheel();
        // Dragging around the wheel recolors live; the changes merge into one undo step.
        MoreColors.ColorChanged += (_, e) =>
        {
            if (!_syncingStyle)
                SetColor(e.NewColor);
        };
    }

    private void ShowColorInWheel()
    {
        _syncingStyle = true;
        MoreColors.Color = ActiveColor;
        _syncingStyle = false;
    }
}
