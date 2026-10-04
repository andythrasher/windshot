namespace Windshot;

internal static class AppIcon
{
    /// <summary>The multi-size .ico shipped next to the exe (also embedded as the exe's icon).</summary>
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Windshot.ico");
}
