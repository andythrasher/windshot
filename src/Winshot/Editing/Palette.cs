using Windows.UI;

namespace Winshot.Editing;

/// <summary>Slightly muted but still playful colors that read well on typical UI screenshots.</summary>
internal static class Palette
{
    public static readonly (string Name, Color Color)[] Colors =
    [
        ("Coral", Color.FromArgb(255, 232, 93, 80)),
        ("Tangerine", Color.FromArgb(255, 240, 145, 60)),
        ("Mustard", Color.FromArgb(255, 233, 190, 70)),
        ("Sage", Color.FromArgb(255, 98, 178, 120)),
        ("Ocean", Color.FromArgb(255, 64, 132, 214)),
        ("Lavender", Color.FromArgb(255, 150, 110, 205)),
        ("Ink", Color.FromArgb(255, 38, 40, 48)),
        ("Cloud", Color.FromArgb(255, 247, 246, 242)),
    ];
}
