using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// "Beautify" export style: the capture sits on a gradient with padding, rounded corners
/// and a soft shadow. Sizes are in DIPs and scale with the capture's DPI.
/// </summary>
internal sealed record Backdrop(Color From, Color To, float Padding, float CornerRadius = 10);

internal static class BackdropPresets
{
    public static readonly (string Name, Color From, Color To)[] All =
    [
        ("Sky", Rgb(0x4F, 0xAC, 0xFE), Rgb(0x00, 0xF2, 0xFE)),
        ("Sunset", Rgb(0xFA, 0x70, 0x9A), Rgb(0xFE, 0xE1, 0x40)),
        ("Grape", Rgb(0xA1, 0x8C, 0xD1), Rgb(0xFB, 0xC2, 0xEB)),
        ("Mint", Rgb(0x43, 0xE9, 0x7B), Rgb(0x38, 0xF9, 0xD7)),
        ("Peach", Rgb(0xF6, 0xD3, 0x65), Rgb(0xFD, 0xA0, 0x85)),
        ("Night", Rgb(0x30, 0xCF, 0xD0), Rgb(0x33, 0x08, 0x67)),
        ("Graphite", Rgb(0x43, 0x43, 0x43), Rgb(0x00, 0x00, 0x00)),
        ("Paper", Rgb(0xF5, 0xF7, 0xFA), Rgb(0xC3, 0xCF, 0xE2)),
    ];

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}
