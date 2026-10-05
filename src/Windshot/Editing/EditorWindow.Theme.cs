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
        // The tint covers the window, over Mica: nearly opaque, so Mica only softens it. (A
        // tinted MicaController backdrop would be nicer, but a custom SystemBackdrop fails in
        // the trimmed build.) The title bar, outside the content, gets the same color.
        if (look.Tint is Color tint)
            Root.Background = new SolidColorBrush(look.Solid ? tint : Color.FromArgb(0xE6, tint.R, tint.G, tint.B));
        if (look.Panel is Color panel)
            LayersPanel.Background = new SolidColorBrush(panel);

        if ((look.TitleBar ?? look.Tint) is Color bar)
        {
            var text = look.TitleText ?? (bar.IsLight() ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            // Caption buttons darken on hover over a light bar and lighten over a dark one.
            var hover = bar.IsLight() || look.TitleBar is not null
                ? Color.FromArgb(255, (byte)(bar.R * 0.85), (byte)(bar.G * 0.85), (byte)(bar.B * 0.85))
                : Color.FromArgb(255, (byte)(bar.R + (255 - bar.R) * 0.12), (byte)(bar.G + (255 - bar.G) * 0.12), (byte)(bar.B + (255 - bar.B) * 0.12));
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
