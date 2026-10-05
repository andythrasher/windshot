using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>The editor's theme (Settings > Appearance): window colors, accent and annotation colors.</summary>
public sealed partial class EditorWindow
{
    private static readonly Color StandardAccent = Color.FromArgb(255, 0, 120, 212);

    private Theme _theme = Themes.All[0];

    /// <summary>The annotation colors on offer (the color menu and keys 1–8).</summary>
    private (string Name, Color Color)[] _palette = Palette.Colors;

    /// <summary>Selection outlines and handles on the canvas, which is the same in light and dark mode.</summary>
    private Color AccentColor => _theme.Light.Accent ?? StandardAccent;

    private void ApplyTheme()
    {
        var appearance = Settings.Current.Appearance;
        _theme = Themes.Find(appearance.Window);
        _palette = Themes.Find(appearance.Colors).Colors;
        if (_theme.IsStandard)
            return;

        if (!_theme.Light.Solid && _theme.Light.Tint is Color light && _theme.Dark.Tint is Color dark)
            SystemBackdrop = new TintedMicaBackdrop(light, dark);

        // Controls take their accent from these. Overriding them here, around the editor's
        // content, recolors the editor alone (the selected tool, sliders, the selected layer).
        var resources = new ResourceDictionary();
        resources.ThemeDictionaries["Light"] = AccentResources(_theme.Light);
        resources.ThemeDictionaries["Dark"] = AccentResources(_theme.Dark);
        resources.ThemeDictionaries["Default"] = AccentResources(_theme.Dark);
        Root.Resources.MergedDictionaries.Add(resources);

        ApplyThemeColors();
        Root.ActualThemeChanged += (_, _) => ApplyThemeColors();
    }

    /// <summary>The colors that depend on light or dark mode and aren't resources.</summary>
    private void ApplyThemeColors()
    {
        var look = Root.ActualTheme == ElementTheme.Dark ? _theme.Dark : _theme.Light;
        CanvasHost.Background = new SolidColorBrush(look.Canvas ?? Microsoft.UI.Colors.Transparent);
        if (look.Solid && look.Tint is Color solid)
            Root.Background = new SolidColorBrush(solid);
        if (look.Panel is Color panel)
            LayersPanel.Background = new SolidColorBrush(panel);

        if (look.TitleBar is Color bar)
        {
            var text = look.TitleText ?? (bar.IsLight() ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            var hover = Color.FromArgb(255, (byte)(bar.R * 0.8), (byte)(bar.G * 0.8), (byte)(bar.B * 0.8));
            var titleBar = AppWindow.TitleBar;
            titleBar.BackgroundColor = bar;
            titleBar.ForegroundColor = text;
            titleBar.InactiveBackgroundColor = bar;
            titleBar.InactiveForegroundColor = text;
            titleBar.ButtonBackgroundColor = bar;
            titleBar.ButtonForegroundColor = text;
            titleBar.ButtonInactiveBackgroundColor = bar;
            titleBar.ButtonInactiveForegroundColor = text;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = text;
            titleBar.ButtonPressedBackgroundColor = hover;
            titleBar.ButtonPressedForegroundColor = text;
        }
    }

    private static ResourceDictionary AccentResources(WindowLook look)
    {
        var resources = new ResourceDictionary();
        if (look.Accent is not Color accent)
            return resources;
        var on = look.OnAccent ?? (accent.IsLight() ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
        // WinUI's own hover and pressed accents are the accent at 90% and 80%.
        var states = new (string Suffix, Color Color)[]
        {
            ("", accent),
            ("PointerOver", Color.FromArgb(230, accent.R, accent.G, accent.B)),
            ("Pressed", Color.FromArgb(204, accent.R, accent.G, accent.B)),
        };

        resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(accent);
        resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(states[1].Color);
        resources["AccentFillColorTertiaryBrush"] = new SolidColorBrush(states[2].Color);
        resources["TextOnAccentFillColorPrimaryBrush"] = new SolidColorBrush(on);
        foreach (var (suffix, color) in states)
        {
            resources["AppBarToggleButtonBackgroundChecked" + suffix] = new SolidColorBrush(color);
            resources["AppBarToggleButtonForegroundChecked" + suffix] = new SolidColorBrush(on);
            resources["ToggleSplitButtonBackgroundChecked" + suffix] = new SolidColorBrush(color);
            resources["ToggleSplitButtonForegroundChecked" + suffix] = new SolidColorBrush(on);
            resources["SliderTrackValueFill" + suffix] = new SolidColorBrush(accent);
            resources["SliderThumbBackground" + suffix] = new SolidColorBrush(color);
            resources["ListViewItemSelectionIndicator" + suffix + "Brush"] = new SolidColorBrush(color);
        }
        return resources;
    }
}
