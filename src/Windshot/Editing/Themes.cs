using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// How the editor window looks in light or dark mode. Null values keep the standard look:
/// plain Mica, the Windows accent color and the standard title bar.
/// </summary>
/// <param name="Tint">The window's color, laid nearly opaque over Mica, or fully opaque when <paramref name="Solid"/>.</param>
/// <param name="Canvas">Around the image, where the window would otherwise show through.</param>
/// <param name="OnAccent">Icons and text on the accent; by default black or white, whichever reads.</param>
/// <param name="Panel">The layers panel's background.</param>
internal sealed record WindowLook(
    Color? Tint = null,
    Color? Canvas = null,
    Color? Accent = null,
    Color? OnAccent = null,
    bool Solid = false,
    Color? TitleBar = null,
    Color? TitleText = null,
    Color? Panel = null);

/// <summary>
/// A theme: a look for the editor window and a set of eight annotation colors. People pick a
/// window and a color set separately, so any theme's colors go with any theme's window.
/// Every color set has the same slots (red, orange, yellow, green, blue, purple, dark, light),
/// so switching sets maps each color to its counterpart.
/// </summary>
internal sealed record Theme(string Name, WindowLook Light, WindowLook Dark, (string Name, Color Color)[] Colors)
{
    /// <summary>The standard Windshot look, which changes nothing.</summary>
    public bool IsStandard => Light == new WindowLook() && Dark == new WindowLook();

    /// <summary>A few colors that stand for the window look in a picker: background, canvas, accent.</summary>
    public Color[] WindowSwatches =>
    [
        Light.Tint ?? Rgb(0xF3F3F3),
        Light.Canvas ?? Rgb(0xEAEAEA),
        Light.Accent ?? Rgb(0x0067C0),
    ];

    internal static Color Rgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

internal static class Themes
{
    private static Color Rgb(uint rgb) => Theme.Rgb(rgb);

    private static WindowLook Tinted(uint tint, uint canvas, uint accent) => new(Rgb(tint), Rgb(canvas), Rgb(accent));

    public static readonly Theme[] All =
    [
        new("Windshot", new(), new(), Palette.Colors),

        new("Ember",
            Tinted(0xF8F0EB, 0xF0E2D9, 0xC2410C),
            Tinted(0x241B18, 0x1B1412, 0xFB923C),
            [("Chili", Rgb(0xD7263D)), ("Ember", Rgb(0xF46036)), ("Saffron", Rgb(0xF2B134)), ("Olive", Rgb(0x7FA650)),
             ("Harbor", Rgb(0x2E86AB)), ("Plum", Rgb(0x8E4585)), ("Char", Rgb(0x2B1D1A)), ("Cream", Rgb(0xFFF4E6))]),

        new("Moss",
            Tinted(0xEFF2EC, 0xE3E8DE, 0x3A7D44),
            Tinted(0x1D221D, 0x161A16, 0x8BCB8F),
            [("Rust", Rgb(0xC4513A)), ("Amber", Rgb(0xD98E32)), ("Straw", Rgb(0xD9C35C)), ("Moss", Rgb(0x5E9B4F)),
             ("Spruce", Rgb(0x2F7F8A)), ("Heather", Rgb(0x8A6FA8)), ("Bark", Rgb(0x2C2A24)), ("Birch", Rgb(0xF4F1E8))]),

        new("Fjord",
            Tinted(0xEEF2F6, 0xE1E7EE, 0x3B6E99),
            Tinted(0x1E242D, 0x171C23, 0x8DB8E0),
            [("Rosehip", Rgb(0xD35F6B)), ("Cloudberry", Rgb(0xE0915E)), ("Lichen", Rgb(0xE8C673)), ("Juniper", Rgb(0x8DBA7E)),
             ("Glacier", Rgb(0x5E92C9)), ("Aurora", Rgb(0xA47DC0)), ("Slate", Rgb(0x2B3240)), ("Snow", Rgb(0xECEFF4))]),

        new("Sorbet",
            Tinted(0xFBF0F4, 0xF4E2EA, 0xB03A7A),
            Tinted(0x261E24, 0x1E171C, 0xF2A0CB),
            [("Raspberry", Rgb(0xF06C8A)), ("Apricot", Rgb(0xF79D5C)), ("Lemon", Rgb(0xF6D365)), ("Mint", Rgb(0x6FCB9F)),
             ("Bluebell", Rgb(0x6FA8F5)), ("Lilac", Rgb(0xB98CF0)), ("Fig", Rgb(0x3A2E39)), ("Meringue", Rgb(0xFFF7FA))]),

        new("Arcade",
            Tinted(0xF2EEF8, 0xE7E0F2, 0x8B2FC9),
            Tinted(0x16121E, 0x100D17, 0xD58CFF),
            [("Laser", Rgb(0xFF3B6B)), ("Blaze", Rgb(0xFF9F1C)), ("Volt", Rgb(0xFFE14D)), ("Acid", Rgb(0x2EF2A0)),
             ("Cyan", Rgb(0x2BC4FF)), ("Ultra", Rgb(0xB35CFF)), ("Void", Rgb(0x14111A)), ("Glow", Rgb(0xF5F3FF))]),

        new("Graphite",
            Tinted(0xEDEDED, 0xE1E1E1, 0x3D4148),
            Tinted(0x1B1B1C, 0x141415, 0xC9CDD3),
            [("Signal", Rgb(0xE5322D)), ("Safety", Rgb(0xFF8A00)), ("Caution", Rgb(0xFFD400)), ("Go", Rgb(0x22A06B)),
             ("Link", Rgb(0x1F6FEB)), ("Iris", Rgb(0x7A4FD8)), ("Carbon", Rgb(0x1F2328)), ("Paper", Rgb(0xFFFFFF))]),

        // A tribute to the Windows 3.1 color scheme of the same name. Garish on purpose.
        new("Hot Dog Stand",
            new(Tint: Rgb(0xFFFF00), Canvas: Rgb(0xFF0000), Accent: Rgb(0x000000), OnAccent: Rgb(0xFFFF00), Solid: true,
                TitleBar: Rgb(0xFF0000), TitleText: Rgb(0xFFFFFF), Panel: Rgb(0xFFFF00)),
            new(Tint: Rgb(0x000000), Canvas: Rgb(0x800000), Accent: Rgb(0xFFFF00), OnAccent: Rgb(0x000000), Solid: true,
                TitleBar: Rgb(0xFF0000), TitleText: Rgb(0xFFFF00), Panel: Rgb(0x000000)),
            [("Ketchup", Rgb(0xFF0000)), ("Orange drink", Rgb(0xFF8000)), ("Mustard", Rgb(0xFFFF00)), ("Relish", Rgb(0x00FF00)),
             ("Slushie", Rgb(0x0000FF)), ("Grape", Rgb(0xFF00FF)), ("Charcoal", Rgb(0x000000)), ("Napkin", Rgb(0xFFFFFF))]),
    ];

    /// <summary>The editor theme in use: the one picked in Settings.</summary>
    public static Theme EffectiveWindow => Find(Settings.Current.Appearance.Window);

    /// <summary>The color set in use: the one picked in Settings (or in the editor's color menu).</summary>
    public static Theme EffectiveColors => Find(Settings.Current.Appearance.Colors);

    /// <summary>The theme with this name, or the standard one.</summary>
    public static Theme Find(string? name) =>
        All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>
    /// The counterpart of a color in another set, so annotations and remembered colors carry
    /// over when the color set changes: Coral becomes Ember's Chili, and so on. Other colors stay.
    /// </summary>
    public static Color InSet(Color color, (string Name, Color Color)[] set)
    {
        if (set.Any(c => c.Color == color))
            return color;
        // Colors earlier versions offered, by their slot: Mustard became Marigold.
        if (color == Rgb(0xE9BE46) && set.Length > 2)
            return set[2].Color;
        foreach (var theme in All)
        {
            int slot = Array.FindIndex(theme.Colors, c => c.Color == color);
            if (slot >= 0 && slot < set.Length)
                return set[slot].Color;
        }
        return color;
    }
}
