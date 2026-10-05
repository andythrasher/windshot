using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Windshot.Editing;

/// <summary>Text fonts, and colors beyond the color set (the color wheel under "More colors…").</summary>
public sealed partial class EditorWindow
{
    /// <summary>Set while showing a value in a picker, so its change event doesn't apply it back.</summary>
    private bool _syncingStyle;

    // ---- Fonts -------------------------------------------------------------------------

    private void InitializeFonts()
    {
        foreach (var (name, family) in TextAnnotation.Fonts)
            FontPicker.Items.Add(FontItem(name, family));
        FontPicker.SelectionChanged += (_, _) =>
        {
            if (!_syncingStyle && FontPicker.SelectedItem is ComboBoxItem { Tag: string family })
                SetFont(family);
        };
    }

    /// <summary>A font in the list, shown in its own typeface. (Items, not binding: see InitializeColorSets.)</summary>
    private static ComboBoxItem FontItem(string name, string family) =>
        new() { Content = name, FontFamily = new FontFamily(family), Tag = family };

    /// <summary>The font picker only appears for the text tool or selected text.</summary>
    private void SyncFont()
    {
        var text = _selected as TextAnnotation;
        FontContainer.Visibility = text is not null || _tool == Tool.Text ? Visibility.Visible : Visibility.Collapsed;
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

    // ---- More colors -------------------------------------------------------------------

    private void InitializeMoreColors()
    {
        MoreColorsButton.Click += (_, _) =>
        {
            bool show = MoreColorsButton.IsChecked == true;
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
