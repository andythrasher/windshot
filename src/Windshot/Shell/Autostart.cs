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

    /// <summary>The exe in a Run command like <c>"C:\path\Windshot.exe" --autostart</c>.</summary>
    private static string ExePath(string command) =>
        command.StartsWith('"') && command.IndexOf('"', 1) is int end and > 0 ? command[1..end] : command.Split(' ')[0];

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

    /// <summary>
    /// If it's on but its exe is gone (the app was moved or deleted), point it here. An entry
    /// for another copy that still exists is left alone, so running a dev build doesn't take
    /// over from the installed one.
    /// </summary>
    public static void Repair()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(Name) is string command && command != Command && !File.Exists(ExePath(command)))
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
