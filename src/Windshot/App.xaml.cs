using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windshot.Capture;
using Windshot.Editing;
using Windshot.Shell;

namespace Windshot;

/// <summary>
/// Tray-resident app: no window at startup, a global hotkey opens the capture overlay,
/// and each capture opens its own editor window.
/// </summary>
public partial class App : Application
{
    private static EventWaitHandle? _captureSignal;
    private HotkeyWindow? _hotkeys;
    private TrayIcon? _tray;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Write($"Unhandled: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: launching again just asks the running instance to capture,
        // so a pinned taskbar icon or a shortcut works as a capture button.
        _captureSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Windshot.Capture", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            _captureSignal.Set();
            Exit();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        ThreadPool.RegisterWaitForSingleObject(_captureSignal,
            (_, _) => dispatcher.TryEnqueue(StartCapture), null, Timeout.Infinite, executeOnlyOnce: false);

        // Stay alive with zero windows open; we only exit from the tray menu.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        Settings.Load();
        _hotkeys = new HotkeyWindow();
        _tray = new TrayIcon(StartCapture, StartTextCapture, StartScrollingCapture, OpenSettings, Quit);
        string hotkeys = ApplyHotkeys();
        WatchSettingsFile(dispatcher);
        Log.Write($"Started; {hotkeys}");
    }

    /// <summary>(Re)registers the hotkeys from settings and shows them in the tray menu.</summary>
    /// <returns>A summary for the log.</returns>
    private string ApplyHotkeys()
    {
        var keys = Settings.Current.Hotkeys;
        _hotkeys!.UnregisterAll();
        string? capture = _hotkeys.TryRegister(keys.Capture, StartCapture) ? keys.Capture : null;
        string? copyText = _hotkeys.TryRegister(keys.CopyText, StartTextCapture) ? keys.CopyText : null;
        string? scrolling = _hotkeys.TryRegister(keys.ScrollingCapture, StartScrollingCapture) ? keys.ScrollingCapture : null;
        _tray!.SetShortcuts(capture, copyText, scrolling);
        return $"hotkeys capture={capture ?? $"'{keys.Capture}' unavailable"} text={copyText ?? $"'{keys.CopyText}' unavailable"}"
            + $" scrolling={scrolling ?? $"'{keys.ScrollingCapture}' unavailable"}";
    }

    private FileSystemWatcher? _settingsWatcher;
    private DispatcherQueueTimer? _settingsReload;

    /// <summary>Hand edits to the settings file apply as soon as it's saved.</summary>
    private void WatchSettingsFile(DispatcherQueue dispatcher)
    {
        // Editors save in bursts (and we swap in a temp file), so wait for things to settle.
        _settingsReload = dispatcher.CreateTimer();
        _settingsReload.Interval = TimeSpan.FromMilliseconds(400);
        _settingsReload.IsRepeating = false;
        _settingsReload.Tick += (_, _) => ReloadSettings();

        _settingsWatcher = new FileSystemWatcher(Path.GetDirectoryName(Settings.FilePath)!, Path.GetFileName(Settings.FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler changed = (_, _) => dispatcher.TryEnqueue(() =>
        {
            _settingsReload.Stop();
            _settingsReload.Start();
        });
        _settingsWatcher.Changed += changed;
        _settingsWatcher.Created += changed;
        _settingsWatcher.Renamed += (s, e) => changed(s, e);
        _settingsWatcher.EnableRaisingEvents = true;
    }

    private void ReloadSettings()
    {
        // Our own saves (e.g. when an editor closes) aren't edits to react to.
        if (DateTime.UtcNow - Settings.LastSavedUtc < TimeSpan.FromSeconds(2))
            return;

        var before = Settings.Current.Hotkeys;
        Settings.Load();
        var after = Settings.Current.Hotkeys;
        if (before.Capture != after.Capture || before.CopyText != after.CopyText || before.ScrollingCapture != after.ScrollingCapture)
            Log.Write($"Settings reloaded; {ApplyHotkeys()}");
        else
            Log.Write("Settings reloaded");
    }

    private static void OpenSettings()
    {
        if (!File.Exists(Settings.FilePath))
            Settings.Save();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Settings.FilePath) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No app is associated with .json; Notepad is always there.
            System.Diagnostics.Process.Start("notepad.exe", $"\"{Settings.FilePath}\"");
        }
    }

    /// <summary>Select a region and copy its text, without opening the editor.</summary>
    private void StartTextCapture()
    {
        try
        {
            CaptureSession.Begin(image => _ = CopyTextAsync(image), hint: "Select text to copy");
        }
        catch (Exception ex)
        {
            Log.Write($"Text capture failed: {ex}");
        }
    }

    private static async Task CopyTextAsync(CapturedImage image)
    {
        string message = await TextRecognizer.CopyToClipboardAsync(image.Pixels, image.Width, image.Height, image.Scale);
        Log.Write($"OCR: {message}");
        Hud.Show(message, image.DesktopBounds, image.Scale);
    }

    /// <summary>Scroll an area to capture more than fits on screen; again while running, finish.</summary>
    private void StartScrollingCapture()
    {
        try
        {
            ScrollingCapture.Begin(OpenEditor);
        }
        catch (Exception ex)
        {
            Log.Write($"Scrolling capture failed: {ex}");
        }
    }

    private void StartCapture()
    {
        try
        {
            CaptureSession.Begin(OpenEditor);
        }
        catch (Exception ex)
        {
            Log.Write($"Capture failed: {ex}");
        }
    }

    internal static void OpenEditor(CapturedImage image)
    {
        // Often runs inside a WinForms mouse handler, which would otherwise swallow exceptions.
        try
        {
            new EditorWindow(image).Activate();
        }
        catch (Exception ex)
        {
            Log.Write($"Opening editor failed: {ex}");
        }
    }

    private void Quit()
    {
        _settingsWatcher?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        Exit();
    }
}
