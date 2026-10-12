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
    private static EventWaitHandle? _settingsSignal;
    private static EventWaitHandle? _openSignal;
    /// <summary>Opens the settings window; in a second launch, asks the running copy to.</summary>
    private const string SettingsArgument = "--settings";
    private HotkeyWindow? _hotkeys;
    private TrayIcon? _tray;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Write($"Unhandled: {e.Exception}");
        // The WinForms parts (tray, overlay, pins) would otherwise show WinForms' own
        // "Unhandled exception" dialog; log it and carry on, like the rest of the app does.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) => Log.Write($"Unhandled (WinForms): {e.Exception}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: launching again just asks the running instance to capture,
        // so a pinned taskbar icon or a shortcut works as a capture button.
        var commandLine = Environment.GetCommandLineArgs();
        bool atSignIn = Autostart.LaunchedAtSignIn(commandLine);
        _captureSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Windshot.Capture", out bool isFirstInstance);
        _settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Windshot.Settings");
        _openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Windshot.Open");
        var files = FilesIn(commandLine);
        if (!isFirstInstance)
        {
            if (files.Count > 0)
            {
                // "Open with" while running: hand the files to the running copy.
                try
                {
                    Directory.CreateDirectory(AppPackage.DataFolder);
                    File.AppendAllLines(OpenRequests, files);
                    _openSignal.Set();
                }
                catch (IOException ex)
                {
                    Log.Write($"Couldn't pass on the files to open: {ex.Message}");
                }
            }
            else if (commandLine.Contains(SettingsArgument))
                _settingsSignal.Set();
            // Launched at sign-in while already running: nothing to do (and no surprise capture).
            else if (!atSignIn)
                _captureSignal.Set();
            Exit();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        ThreadPool.RegisterWaitForSingleObject(_captureSignal,
            (_, _) => dispatcher.TryEnqueue(StartCapture), null, Timeout.Infinite, executeOnlyOnce: false);
        ThreadPool.RegisterWaitForSingleObject(_settingsSignal,
            (_, _) => dispatcher.TryEnqueue(OpenSettings), null, Timeout.Infinite, executeOnlyOnce: false);
        ThreadPool.RegisterWaitForSingleObject(_openSignal,
            (_, _) => dispatcher.TryEnqueue(OpenRequested), null, Timeout.Infinite, executeOnlyOnce: false);

        // Stay alive with zero windows open; we only exit from the tray menu.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        Settings.Load();
        _hotkeys = new HotkeyWindow();
        _tray = new TrayIcon(StartCapture, StartTextCapture, StartScrollingCapture, StartRecording, StartDelayedCapture, CancelDelayedCapture, OpenFile, OpenSettings, Quit);
        string hotkeys = ApplyHotkeys().Summary;
        WatchSettingsFile(dispatcher);
        ScreenRecording.CleanUp();
        int pins = PinStore.RestoreAll(OpenEditor);
        Autostart.Repair();
        Log.Write($"Started{(AppPackage.IsPackaged ? " (Store package)" : "")}{(atSignIn ? " at sign-in" : "")}; {hotkeys}" + (pins > 0 ? $"; restored {pins} pins" : ""));
        // Started to open files ("Open with"): just those, no welcome.
        if (files.Count > 0)
        {
            foreach (var path in files)
                _ = OpenPathAsync(path);
        }
        else if (commandLine.Contains(SettingsArgument))
            OpenSettings();
        else if (!atSignIn && Settings.Current.ShowWelcome && !commandLine.Contains(NoWelcomeArgument))
            _ = WelcomeAsync();
    }

    /// <summary>For scripts that restart Windshot: start without the welcome window.</summary>
    private const string NoWelcomeArgument = "--no-welcome";

    private static async Task WelcomeAsync()
    {
        // The very first time, Start with Windows starts out on: a tray app that isn't there
        // after a restart is soon forgotten. The welcome window shows it, with its switch.
        if (Settings.IsFirstRun)
            await Autostart.SetAsync(true);
        WelcomeWindow.ShowOrActivate();
    }

    /// <summary>Which shortcuts registered. An empty shortcut is off, which isn't a failure.</summary>
    internal sealed record HotkeyStatus(bool Capture, bool CopyText, bool ScrollingCapture, bool Record, string Summary);

    internal static App Instance => (App)Current;

    /// <summary>(Re)registers the hotkeys from settings and shows them in the tray menu.</summary>
    internal HotkeyStatus ApplyHotkeys()
    {
        var keys = Settings.Current.Hotkeys;
        _hotkeys!.UnregisterAll();
        bool Register(string shortcut, Action action) => string.IsNullOrWhiteSpace(shortcut) || _hotkeys.TryRegister(shortcut, action);
        bool capture = Register(keys.Capture, StartCapture);
        bool copyText = Register(keys.CopyText, StartTextCapture);
        bool scrolling = Register(keys.ScrollingCapture, StartScrollingCapture);
        bool record = Register(keys.Record, StartRecording);
        string? Label(string shortcut, bool ok) => ok ? shortcut : null;
        _tray!.SetShortcuts(Label(keys.Capture, capture), Label(keys.CopyText, copyText), Label(keys.ScrollingCapture, scrolling), Label(keys.Record, record));
        string Describe(string shortcut, bool ok) => ok ? (shortcut is "" ? "off" : shortcut) : $"'{shortcut}' unavailable";
        return new HotkeyStatus(capture, copyText, scrolling, record,
            $"hotkeys capture={Describe(keys.Capture, capture)} text={Describe(keys.CopyText, copyText)} scrolling={Describe(keys.ScrollingCapture, scrolling)} record={Describe(keys.Record, record)}");
    }

    /// <summary>Lets go of every hotkey, so the settings window can record any combination (call ApplyHotkeys after).</summary>
    internal void SuspendHotkeys() => _hotkeys!.UnregisterAll();

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
        if (before.Capture != after.Capture || before.CopyText != after.CopyText || before.ScrollingCapture != after.ScrollingCapture || before.Record != after.Record)
            Log.Write($"Settings reloaded; {ApplyHotkeys().Summary}");
        else
            Log.Write("Settings reloaded");
        SettingsWindow.Refresh(); // show the hand edit if the window is open
        WelcomeWindow.Refresh();
    }

    private static void OpenSettings() => SettingsWindow.ShowOrActivate();

    /// <summary>Opens settings.json in the default editor (Notepad if .json has none), for anything the window doesn't cover.</summary>
    internal static void OpenSettingsFile()
    {
        if (!File.Exists(Settings.FilePath))
            Settings.Save();
        try
        {
            // With no app for .json, opening it would show Windows' "How do you want to open
            // this file?" picker; Notepad is always there.
            if (HasAppFor(".json"))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Settings.FilePath) { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start("notepad.exe", $"\"{Settings.FilePath}\"");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Write($"Couldn't open the settings file: {ex.Message}");
        }
    }

    private static bool HasAppFor(string extension)
    {
        const int ASSOCF_INIT_IGNOREUNKNOWN = 0x400;
        const int ASSOCSTR_EXECUTABLE = 2;
        var exe = new char[1024];
        uint length = (uint)exe.Length;
        if (AssocQueryString(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_EXECUTABLE, extension, null, exe, ref length) != 0)
            return false;
        // For unknown types Windows can still name its own picker as the "app".
        string path = new string(exe, 0, (int)Math.Max(0, length - 1));
        return !Path.GetFileName(path).Equals("OpenWith.exe", StringComparison.OrdinalIgnoreCase);
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, char[]? output, ref uint length);

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
            ScrollingCapture.Begin(OpenCapture);
        }
        catch (Exception ex)
        {
            Log.Write($"Scrolling capture failed: {ex}");
        }
    }

    /// <summary>Pick an area and record it; while recording, stop and open the recording.</summary>
    private void StartRecording()
    {
        try
        {
            ScreenRecording.Begin(OpenEditor, recording => _tray?.SetRecording(recording));
        }
        catch (Exception ex)
        {
            Log.Write($"Recording failed: {ex}");
        }
    }

    internal void StartCapture()
    {
        try
        {
            CaptureSession.Begin(OpenCapture);
        }
        catch (Exception ex)
        {
            Log.Write($"Capture failed: {ex}");
        }
    }

    private System.Windows.Forms.Timer? _delay;

    /// <summary>
    /// Captures after a few seconds, for menus and hover states that a keypress would close: a
    /// countdown by the tray, then the usual overlay, over the screen as it is then.
    /// </summary>
    private void StartDelayedCapture(int seconds)
    {
        CancelDelayedCapture();
        Settings.Current.Capture.Delay = seconds;
        Settings.Save();
        var work = Monitors.Primary?.WorkingArea ?? System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        double scale = SelectionOverlay.GetMonitorScale(work);
        // Bottom right, by the tray, out of the way of what's being set up.
        var corner = new System.Drawing.Rectangle(work.Right - (int)(260 * scale), work.Bottom - (int)(90 * scale), (int)(240 * scale), (int)(70 * scale));
        int left = seconds;
        Hud.Show($"Capturing in {left}…", corner, scale);
        _tray?.SetDelaying(true);
        _delay = new System.Windows.Forms.Timer { Interval = 1000 };
        _delay.Tick += (_, _) =>
        {
            if (--left > 0)
            {
                Hud.Show($"Capturing in {left}…", corner, scale);
                return;
            }
            CancelDelayedCapture();
            // Give the countdown a moment to leave the screen before it's captured.
            var go = new System.Windows.Forms.Timer { Interval = 150 };
            go.Tick += (_, _) =>
            {
                go.Dispose();
                StartCapture();
            };
            go.Start();
        };
        _delay.Start();
    }

    private void CancelDelayedCapture()
    {
        _delay?.Dispose();
        _delay = null;
        Hud.Hide();
        _tray?.SetDelaying(false);
    }

    /// <summary>The tray's "Open image or video…".</summary>
    private void OpenFile()
    {
        var images = string.Join(";", OpenedFile.ImageTypes.Select(t => "*" + t));
        var videos = string.Join(";", OpenedFile.VideoTypes.Select(t => "*" + t));
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Open an image or video in Windshot",
            Filter = $"Images and videos|{images};{videos}|Images|{images}|Videos|{videos}",
            Multiselect = true,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            foreach (var path in dialog.FileNames)
                _ = OpenPathAsync(path);
        }
    }

    /// <summary>Opens a file in a new editor; says so if it can't.</summary>
    private static async Task OpenPathAsync(string path)
    {
        try
        {
            var image = await OpenedFile.LoadAsync(path);
            Log.Write($"Opened {path} ({image.Width}x{image.Height}{(image.Video is { } v ? $", {v.Length.TotalSeconds:F1} s video" : "")})");
            OpenEditor(image);
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't open {path}: {ex}");
            var work = Monitors.Primary?.WorkingArea ?? System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            Hud.Show(ex is InvalidDataException ? ex.Message : $"Couldn't open {Path.GetFileName(path)}", work, SelectionOverlay.GetMonitorScale(work));
        }
    }

    /// <summary>Files given on the command line ("Open with", or a script), that Windshot can open.</summary>
    private static List<string> FilesIn(string[] commandLine) =>
        commandLine.Skip(1).Where(arg => !arg.StartsWith("--") && OpenedFile.CanOpen(arg) && File.Exists(arg)).Select(Path.GetFullPath).ToList();

    /// <summary>Where a second launch leaves the files it was given, for the running copy to open.</summary>
    private static string OpenRequests => Path.Combine(AppPackage.DataFolder, "open-requests.txt");

    private static void OpenRequested()
    {
        try
        {
            if (!File.Exists(OpenRequests))
                return;
            var paths = File.ReadAllLines(OpenRequests);
            File.Delete(OpenRequests);
            foreach (var path in paths.Where(p => p.Length > 0).Distinct())
                _ = OpenPathAsync(path);
        }
        catch (IOException ex)
        {
            Log.Write($"Couldn't read the files to open: {ex.Message}");
        }
    }

    /// <summary>A new screenshot: copied right away if that's turned on, then into the editor.</summary>
    private static void OpenCapture(CapturedImage image)
    {
        if (Settings.Current.Capture.CopyRightAway)
            _ = CopyCaptureAsync(image);
        OpenEditor(image);
    }

    private static async Task CopyCaptureAsync(CapturedImage image)
    {
        try
        {
            using var bitmap = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(), image.Pixels,
                image.Width, image.Height, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Premultiplied);
            var png = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await bitmap.SaveAsync(png, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
            png.Seek(0);
            if (!ClipboardWriter.TrySetPng(png))
                Hud.Show("Couldn't copy: the clipboard is busy", image.DesktopBounds, image.Scale);
        }
        catch (Exception ex)
        {
            Log.Write($"Copying the capture failed: {ex}");
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
        // Quitting closes everything at once; recordings that aren't saved yet would be lost
        // without the question closing their editor asks, so ask here.
        int unsaved = Editing.EditorWindow.Open.Count(editor => editor.HasUnsavedVideo) + (ScreenRecording.IsRecording ? 1 : 0);
        if (unsaved > 0)
        {
            string what = unsaved == 1 ? "A recording hasn't been saved. If you quit, it's gone." : $"{unsaved} recordings haven't been saved. If you quit, they're gone.";
            var answer = System.Windows.Forms.MessageBox.Show(what, "Quit Windshot?", System.Windows.Forms.MessageBoxButtons.OKCancel,
                System.Windows.Forms.MessageBoxIcon.Warning, System.Windows.Forms.MessageBoxDefaultButton.Button2);
            if (answer != System.Windows.Forms.DialogResult.OK)
                return;
        }
        // Pins stay for next time; only closing one yourself removes it.
        PinWindow.AppExiting = true;
        _settingsWatcher?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        Exit();
    }
}
