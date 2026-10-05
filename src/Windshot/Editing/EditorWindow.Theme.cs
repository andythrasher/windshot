using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>The editor's theme (Settings > Appearance): window colors, accent and annotation colors.</summary>
public sealed partial class EditorWindow
{
    private static readonly Color StandardAccent = Color.FromArgb(255, 0, 120, 212);

    private ThemedWindow _theming = null!;

    /// <summary>The annotation colors on offer (the color menu and keys 1–8).</summary>
    private (string Name, Color Color)[] _palette = Palette.Colors;

    /// <summary>Selection outlines and handles on the canvas, which is the same in light and dark mode.</summary>
    private Color AccentColor => _theming.Theme.Light.Accent ?? StandardAccent;

    private void ApplyTheme()
    {
        var appearance = Settings.Current.Appearance;
        _palette = Themes.Find(appearance.Colors).Colors;
        _theming = new ThemedWindow(this, Root, look =>
        {
            CanvasHost.Background = new SolidColorBrush(look.Canvas ?? Microsoft.UI.Colors.Transparent);
            if (look.Panel is Color panel)
                LayersPanel.Background = new SolidColorBrush(panel);
        });
        _theming.Apply(Themes.Find(appearance.Window));
    }
}
