using Windows.UI;

namespace Winshot.Editing;

internal static class ColorExtensions
{
    public static bool IsLight(this Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255 > 0.6;

    /// <summary>White or near-black, whichever stands out against this color.</summary>
    public static Color Contrasting(this Color c) =>
        c.IsLight() ? Color.FromArgb(255, 24, 24, 24) : Color.FromArgb(255, 255, 255, 255);
}
