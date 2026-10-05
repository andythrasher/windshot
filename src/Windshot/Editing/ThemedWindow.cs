using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// Gives a window a theme's colors: its background over Mica, title bar and accent. The theme
/// can change while the window is open (the settings window previews themes this way).
/// </summary>
internal sealed class ThemedWindow
{
    private readonly Window _window;
    private readonly Panel _root;
    /// <summary>Window-specific colors, e.g. the editor's canvas; called with each change.</summary>
    private readonly Action<WindowLook>? _applyMore;
    private ResourceDictionary? _resources;
    /// <summary>Flyout contents: they're shown outside the window's content, so out of reach of its resources.</summary>
    private readonly List<(FrameworkElement Element, ResourceDictionary? Resources)> _extras = new();

    public ThemedWindow(Window window, Panel root, Action<WindowLook>? applyMore = null)
    {
        _window = window;
        _root = root;
        _applyMore = applyMore;
        root.ActualThemeChanged += (_, _) => ApplyColors();
    }

    public Theme Theme { get; private set; } = Themes.All[0];

    /// <summary>Also gives this element (a flyout's content) the theme's accent. Call before <see cref="Apply"/>.</summary>
    public void Include(FrameworkElement element) => _extras.Add((element, null));

    public void Apply(Theme theme)
    {
        Theme = theme;
        // Controls take their accent from these. Overriding them around the window's content
        // recolors this window alone (selected tool, switches, sliders, links).
        _resources = Replace(_root, _resources, theme);
        for (int i = 0; i < _extras.Count; i++)
            _extras[i] = (_extras[i].Element, Replace(_extras[i].Element, _extras[i].Resources, theme));

        if (_root.IsLoaded)
        {
            // Controls already on screen keep the resources they looked up. Flipping the theme
            // and back makes them look again.
            _root.RequestedTheme = _root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            _root.RequestedTheme = ElementTheme.Default;
        }
        ApplyColors();
    }

    /// <summary>Swaps the accent resources an element has for this theme's (none for the standard theme).</summary>
    private static ResourceDictionary? Replace(FrameworkElement element, ResourceDictionary? old, Theme theme)
    {
        if (old is not null)
            element.Resources.MergedDictionaries.Remove(old);
        if (theme.IsStandard)
            return null;
        var resources = new ResourceDictionary();
        resources.ThemeDictionaries["Light"] = AccentResources(theme.Light);
        resources.ThemeDictionaries["Dark"] = AccentResources(theme.Dark);
        resources.ThemeDictionaries["Default"] = AccentResources(theme.Dark);
        element.Resources.MergedDictionaries.Add(resources);
        return resources;
    }

    /// <summary>The colors that depend on light or dark mode and aren't resources.</summary>
    private void ApplyColors()
    {
        var look = _root.ActualTheme == ElementTheme.Dark ? Theme.Dark : Theme.Light;

        // Nearly opaque over Mica, so Mica only softens it. (A tinted MicaController backdrop
        // would be nicer, but a custom SystemBackdrop fails in the trimmed build.)
        _root.Background = look.Tint is Color tint
            ? new SolidColorBrush(look.Solid ? tint : Color.FromArgb(0xE6, tint.R, tint.G, tint.B))
            : null;

        // The title bar is outside the content, so it gets the same color separately.
        var titleBar = _window.AppWindow.TitleBar;
        if ((look.TitleBar ?? look.Tint) is Color bar)
        {
            var text = look.TitleText ?? (bar.IsLight() ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            // Caption buttons darken on hover over a light or bold bar, and lighten over a dark one.
            var hover = bar.IsLight() || look.TitleBar is not null
                ? Color.FromArgb(255, (byte)(bar.R * 0.85), (byte)(bar.G * 0.85), (byte)(bar.B * 0.85))
                : Color.FromArgb(255, (byte)(bar.R + (255 - bar.R) * 0.12), (byte)(bar.G + (255 - bar.G) * 0.12), (byte)(bar.B + (255 - bar.B) * 0.12));
            SetTitleBar(titleBar, bar, text, hover);
        }
        else
        {
            SetTitleBar(titleBar, null, null, null); // back to the standard title bar
        }

        _applyMore?.Invoke(look);
    }

    private static void SetTitleBar(Microsoft.UI.Windowing.AppWindowTitleBar titleBar, Color? bar, Color? text, Color? hover)
    {
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
        resources["ComboBoxItemPillFillBrush"] = new SolidColorBrush(accent);
        foreach (var (suffix, color) in states)
        {
            resources["AppBarToggleButtonBackgroundChecked" + suffix] = new SolidColorBrush(color);
            resources["AppBarToggleButtonForegroundChecked" + suffix] = new SolidColorBrush(on);
            resources["AccentButtonBackground" + suffix] = new SolidColorBrush(color);
            resources["AccentButtonForeground" + suffix] = new SolidColorBrush(on);
            resources["AccentButtonBorderBrush" + suffix] = new SolidColorBrush(color);
            resources["ToggleButtonBackgroundChecked" + suffix] = new SolidColorBrush(color);
            resources["ToggleButtonForegroundChecked" + suffix] = new SolidColorBrush(on);
            resources["ToggleButtonBorderBrushChecked" + suffix] = new SolidColorBrush(color);
            resources["ToggleSplitButtonBackgroundChecked" + suffix] = new SolidColorBrush(color);
            resources["ToggleSplitButtonForegroundChecked" + suffix] = new SolidColorBrush(on);
            resources["SliderTrackValueFill" + suffix] = new SolidColorBrush(accent);
            resources["SliderThumbBackground" + suffix] = new SolidColorBrush(color);
            resources["ListViewItemSelectionIndicator" + suffix + "Brush"] = new SolidColorBrush(color);
            resources["ToggleSwitchFillOn" + suffix] = new SolidColorBrush(color);
            resources["ToggleSwitchStrokeOn" + suffix] = new SolidColorBrush(color);
            resources["ToggleSwitchKnobFillOn" + suffix] = new SolidColorBrush(on);
            resources["HyperlinkButtonForeground" + suffix] = new SolidColorBrush(color);
        }
        return resources;
    }
}
