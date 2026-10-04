using Windows.UI;

namespace Winshot.Editing;

internal static class Palette
{
    public static readonly (string Name, Color Color)[] Colors =
    [
        ("Red", Color.FromArgb(255, 255, 59, 48)),
        ("Orange", Color.FromArgb(255, 255, 149, 0)),
        ("Yellow", Color.FromArgb(255, 255, 204, 0)),
        ("Green", Color.FromArgb(255, 52, 199, 89)),
        ("Blue", Color.FromArgb(255, 0, 122, 255)),
        ("Purple", Color.FromArgb(255, 175, 82, 222)),
        ("Black", Color.FromArgb(255, 28, 28, 30)),
        ("White", Color.FromArgb(255, 255, 255, 255)),
    ];
}
