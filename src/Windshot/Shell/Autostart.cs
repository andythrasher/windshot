using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Windshot.Shell;

/// <summary>
/// "Start with Windows", so Windshot is in the tray (and restores its pins) after signing in.
/// The Store app uses its package's startup task (declared in Package.appxmanifest); the
/// unpackaged app uses the per-user Run entry. Either way Windows keeps the only record, so
/// Task Manager's Startup apps and this setting always agree.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Where Task Manager and Settings record that the user turned a startup app off.</summary>
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "Windshot";
    private const string TaskId = "WindshotStartup";
    /// <summary>Marks the unpackaged app's launch at sign-in.</summary>
    public const string Argument = "--autostart";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    /// <summary>The exe in a Run command like <c>"C:\path\Windshot.exe" --autostart</c>.</summary>
    private static string ExePath(string command) =>
        command.StartsWith('"') && command.IndexOf('"', 1) is int end and > 0 ? command[1..end] : command.Split(' ')[0];

    /// <summary>Whether this launch is Windows starting Windshot at sign-in.</summary>
    public static bool LaunchedAtSignIn(string[] commandLine)
    {
        if (!AppPackage.IsPackaged)
            return commandLine.Contains(Argument);
        try
        {
            return Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind
                == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't tell how Windshot was started: {ex.Message}");
            return false;
        }
    }

    /// <summary>On, and not switched off in Task Manager or Settings.</summary>
    public static async Task<bool> IsEnabledAsync()
    {
        try
        {
            if (AppPackage.IsPackaged)
                return (await StartupTask.GetAsync(TaskId)).State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is not string)
                return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // The first byte is odd when disabled (3), even when enabled (2 or 6).
            return approved?.GetValue(Name) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't read Start with Windows: {ex.Message}");
            return false;
        }
    }

    /// <returns>Null if it worked, or why not, for the user (e.g. it was turned off in Windows Settings).</returns>
    public static async Task<string?> SetAsync(bool enabled)
    {
        try
        {
            string? problem = AppPackage.IsPackaged ? await SetTaskAsync(enabled) : SetRunEntry(enabled);
            Log.Write(problem is null ? $"Start with Windows {(enabled ? "on" : "off")}" : $"Start with Windows: {problem}");
            return problem;
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't change Start with Windows: {ex.Message}");
            return "Couldn't change this setting.";
        }
    }

    private static async Task<string?> SetTaskAsync(bool enabled)
    {
        var task = await StartupTask.GetAsync(TaskId);
        if (!enabled)
        {
            if (task.State == StartupTaskState.EnabledByPolicy)
                return "Your organization keeps this on.";
            task.Disable();
            return null;
        }
        // Once switched off in Task Manager or Settings, only the user can turn it back on there.
        var state = task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy ? task.State : await task.RequestEnableAsync();
        return state switch
        {
            StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => null,
            StartupTaskState.DisabledByUser => "It was turned off in Windows. Turn it on in Settings > Apps > Startup.",
            StartupTaskState.DisabledByPolicy => "Your organization has turned this off.",
            _ => "Windows didn't turn it on.",
        };
    }

    private static string? SetRunEntry(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            run.SetValue(Name, Command);
        else
            run.DeleteValue(Name, throwOnMissingValue: false);
        // Turning it on here overrides an earlier "disabled" from Task Manager.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(Name, throwOnMissingValue: false);
        return null;
    }

    /// <summary>
    /// Unpackaged only: if it's on but its exe is gone (the app was moved or deleted), point it
    /// here. An entry for another copy that still exists is left alone, so running a dev build
    /// doesn't take over from the installed one.
    /// </summary>
    public static void Repair()
    {
        if (AppPackage.IsPackaged)
            return;
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
