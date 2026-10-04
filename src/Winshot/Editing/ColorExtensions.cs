using Windows.UI;

namespace Winshot.Editing;

internal static class ColorExtensions
{
    public static bool IsLight(this Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255 > 0.6;

    /// <summary>White or near-black, whichever stands out against this color.</summary>
    public static Color Contrasting(this Color c) =>
        c.IsLight() ? Color.FromArgb(255, 24, 24, 24) : Color.FromArgb(255, 255, 255, 255);

    public static string ToHex(this Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Parses "#RRGGBB" (the # is optional).</summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        string hex = (text ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int value))
            return false;
        color = Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }
}
