namespace Windshot;

/// <summary>
/// Minimal append-only log, windshot.log in <see cref="AppPackage.DataFolder"/>. Once it passes 1 MB it
/// becomes windshot.old.log and a new one starts, so it never grows without bound.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;

    private static readonly string Path = System.IO.Path.Combine(AppPackage.DataFolder, "windshot.log");

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        // Also called from the scrolling capture's worker thread.
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var file = new FileInfo(Path);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(Path, System.IO.Path.ChangeExtension(Path, ".old.log"), overwrite: true);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
