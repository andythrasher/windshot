using System.Runtime.InteropServices;

namespace Windshot;

/// <summary>
/// Whether this copy runs from the Microsoft Store package (MSIX) or unpackaged (the dev build
/// and tools/install.ps1), and where each keeps its files.
/// </summary>
internal static class AppPackage
{
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    public static bool IsPackaged { get; } = DetectPackage();

    // Static properties initialize in this order, so this one has to come before DataFolder.
    private static string UnpackagedDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Windshot");

    /// <summary>
    /// Settings, pins and the log. Unpackaged: %LOCALAPPDATA%\Windshot. Packaged: the package's
    /// own LocalState folder. (Windows quietly redirects a packaged app's writes to
    /// %LOCALAPPDATA% into a private copy that other apps can't see, which would break opening
    /// settings.json in Notepad; LocalState is a real folder everyone can open.)
    /// </summary>
    public static string DataFolder { get; } = IsPackaged
        ? Windows.Storage.ApplicationData.Current.LocalFolder.Path
        : UnpackagedDataFolder;

    private static bool DetectPackage()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
