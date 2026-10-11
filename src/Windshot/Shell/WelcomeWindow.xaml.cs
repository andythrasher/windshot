using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windshot.Editing;

namespace Windshot.Shell;

/// <summary>
/// Shown when Windshot is started by hand (never at sign-in), since a tray app otherwise seems
/// not to have started: where it lives, its shortcuts, the editor theme and Start with Windows.
/// "Show this when Windshot starts" turns it off; Settings can turn it back on.
/// </summary>
public sealed partial class WelcomeWindow : Window
{
    private static WelcomeWindow? _current;

    private readonly ShortcutEditor _shortcuts;
    private readonly ThemedWindow _theming;
    /// <summary>Set while showing values, so the controls' change events don't save them straight back.</summary>
    private bool _loading;

    private WelcomeWindow()
    {
        InitializeComponent();
        _theming = new ThemedWindow(this, Root);
        _shortcuts = new ShortcutEditor(CaptureShortcut, CaptureReset, CaptureProblem,
            ScrollingShortcut, ScrollingReset, ScrollingProblem,
            CopyTextShortcut, CopyTextReset, CopyTextProblem,
            RecordShortcut, RecordReset, RecordProblem, changed: SettingsWindow.Refresh);

        if (File.Exists(AppIcon.FilePath))
        {
            Icon.Source = new BitmapImage(new Uri(AppIcon.FilePath)) { DecodePixelWidth = 112 };
            TrayIcon.Source = new BitmapImage(new Uri(AppIcon.FilePath)) { DecodePixelWidth = 40 };
            AppWindow.SetIcon(AppIcon.FilePath);
        }
        TrayClock.Text = DateTime.Now.ToShortTimeString();

        // Added as items rather than bound: binding a .NET list fails in the trimmed build.
        foreach (var theme in Themes.All)
            WindowTheme.Items.Add(theme.Name);
        WindowTheme.SelectionChanged += (_, _) =>
        {
            if (WindowTheme.SelectedIndex < 0)
                return;
            var theme = Themes.All[WindowTheme.SelectedIndex];
            SettingsWindow.ShowStrip(WindowThemePreview, theme.WindowSwatches, 18);
            _theming.Apply(theme);
            if (_loading || theme.Name == Settings.Current.Appearance.Window)
                return;
            Settings.Current.Appearance.Window = theme.Name;
            Settings.Save();
            SettingsWindow.Refresh();
        };
        StartWithWindows.Toggled += async (_, _) =>
        {
            if (_loading)
                return;
            string? problem = await Autostart.SetAsync(StartWithWindows.IsOn);
            StartWithWindowsProblem.Text = problem ?? "";
            StartWithWindowsProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            await LoadStartWithWindowsAsync(); // show what it really is
            SettingsWindow.Refresh();
        };
        ShowAtStart.Click += (_, _) =>
        {
            Settings.Current.ShowWelcome = ShowAtStart.IsChecked == true;
            Settings.Save();
            SettingsWindow.Refresh();
        };
        Done.Click += (_, _) => Close();
        TryCapture.Click += (_, _) =>
        {
            Close();
            // Give the window a moment to leave the screen, so it isn't in the capture.
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(250);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => App.Instance.StartCapture();
            timer.Start();
        };

        Load();
        SizeAndCenter();
        Closed += (_, _) =>
        {
            _shortcuts.Closed();
            _current = null;
        };
    }

    public static void ShowOrActivate()
    {
        _current ??= new WelcomeWindow();
        _current.Activate();
    }

    /// <summary>Shows changes made in the settings window (or the settings file).</summary>
    public static void Refresh() => _current?.Load();

    private void Load()
    {
        _loading = true;
        _shortcuts.Load();
        WindowTheme.SelectedIndex = Array.IndexOf(Themes.All, Themes.Find(Settings.Current.Appearance.Window));
        ShowAtStart.IsChecked = Settings.Current.ShowWelcome;
        _loading = false;
        _ = LoadStartWithWindowsAsync();
    }

    private async Task LoadStartWithWindowsAsync()
    {
        bool on = await Autostart.IsEnabledAsync();
        _loading = true;
        StartWithWindows.IsOn = on;
        _loading = false;
    }

    private void SizeAndCenter()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        int width = (int)(640 * scale), height = Math.Min((int)(820 * scale), area.Height - (int)(40 * scale));
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
