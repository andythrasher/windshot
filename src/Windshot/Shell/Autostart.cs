using Microsoft.Win32;

namespace Windshot.Shell;

/// <summary>
/// "Start with Windows": the per-user Run entry, so Windshot is in the tray (and restores its
/// pins) after signing in. The registry is the only record, so Task Manager's Startup apps
/// and this setting always agree.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Where Task Manager and Settings record that the user turned a startup app off.</summary>
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "Windshot";
    public const string Argument = "--autostart";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    /// <summary>On, and not switched off in Task Manager or Settings.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is not string)
                return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // The first byte is odd when disabled (3), even when enabled (2 or 6).
            return approved?.GetValue(Name) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                run.SetValue(Name, Command);
            else
                run.DeleteValue(Name, throwOnMissingValue: false);
            // Turning it on here overrides an earlier "disabled" from Task Manager.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(Name, throwOnMissingValue: false);
            Log.Write($"Start with Windows {(enabled ? "on" : "off")}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Write($"Couldn't change Start with Windows: {ex.Message}");
        }
    }

    /// <summary>If it's on but points at another copy of the exe (a moved or rebuilt app), point it here.</summary>
    public static void Repair()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(Name) is string command && command != Command)
            {
                run.SetValue(Name, Command);
                Log.Write($"Start with Windows now points at {Environment.ProcessPath}");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Write($"Couldn't update Start with Windows: {ex.Message}");
        }
    }
}
