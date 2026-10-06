using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// The editor's theme (Settings > Appearance): window colors, accent and annotation colors.
/// Themes other than the standard one, and the extras, are for supporters (see <see cref="Supporter"/>).
/// </summary>
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
        _palette = Themes.EffectiveColors.Colors;
        _theming = new ThemedWindow(this, Root, look =>
        {
            CanvasHost.Background = new SolidColorBrush(look.Canvas ?? Microsoft.UI.Colors.Transparent);
            if (look.Panel is Color panel)
                LayersPanel.Background = new SolidColorBrush(panel);
        });
        _theming.Include(ColorFlyoutContent);
        _theming.Include(BeautifyFlyoutContent);
        _theming.Include(StyleFlyoutContent);
        _theming.Apply(Themes.EffectiveWindow);

        Supporter.Changed += OnSupporterChanged;
        Closed += (_, _) => Supporter.Changed -= OnSupporterChanged;
    }

    /// <summary>Just became a supporter (or the license went away): the extras change right here.</summary>
    private void OnSupporterChanged()
    {
        _theming.Apply(Themes.EffectiveWindow);
        SetColorSet(Themes.EffectiveColors);
        ColorSetPicker.SelectedIndex = Array.IndexOf(Themes.All, Themes.EffectiveColors);
        ColorSetRow.Visibility = Supporter.IsUnlocked ? Visibility.Visible : Visibility.Collapsed;
        Canvas.Invalidate();
    }

    /// <summary>Asks to become a supporter for an extra.</summary>
    /// <returns>Whether the extras are unlocked now.</returns>
    private Task<bool> RequireSupporter(string feature) =>
        Shell.SupporterPrompt.ShowAsync(this, Root.XamlRoot, feature);
}
