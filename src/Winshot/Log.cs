namespace Winshot;

/// <summary>Minimal append-only log at %LOCALAPPDATA%\Winshot\winshot.log.</summary>
internal static class Log
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winshot", "winshot.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }
}
