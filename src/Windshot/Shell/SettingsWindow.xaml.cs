using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windshot.Editing;

namespace Windshot.Shell;

/// <summary>
/// The settings window (tray menu → Settings…). Every change is saved and applied at once.
/// Anything not shown here is in settings.json, linked at the bottom.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private static SettingsWindow? _current;

    private readonly ShortcutEditor _shortcuts;
    /// <summary>Set while showing values, so the controls' change events don't save them straight back.</summary>
    private bool _loading;

    private readonly ThemedWindow _theming;

    private SettingsWindow()
    {
        InitializeComponent();
        _theming = new ThemedWindow(this, Root);
        _shortcuts = new ShortcutEditor(CaptureShortcut, CaptureReset, CaptureProblem,
            ScrollingShortcut, ScrollingReset, ScrollingProblem,
            CopyTextShortcut, CopyTextReset, CopyTextProblem,
            RecordShortcut, RecordReset, RecordProblem, changed: WelcomeWindow.Refresh);

        // Added as items rather than bound: binding a .NET list fails in the trimmed build.
        foreach (var preset in BackdropPresets.All)
            BeautifyPreset.Items.Add(preset.Name);
        foreach (var theme in Themes.All)
        {
            WindowTheme.Items.Add(theme.Name);
            ColorSet.Items.Add(theme.Name);
        }
        WindowTheme.SelectionChanged += (_, _) =>
        {
            if (WindowTheme.SelectedIndex < 0)
                return;
            var theme = Themes.All[WindowTheme.SelectedIndex];
            ShowStrip(WindowThemePreview, theme.WindowSwatches, 18);
            _theming.Apply(theme); // this window previews the editor theme
            if (_loading || theme.Name == Settings.Current.Appearance.Window)
                return;
            Save(s => s.Appearance.Window = theme.Name);
            WelcomeWindow.Refresh();
        };
        ColorSet.SelectionChanged += (_, _) =>
        {
            if (ColorSet.SelectedIndex < 0)
                return;
            var theme = Themes.All[ColorSet.SelectedIndex];
            ShowStrip(ColorSetPreview, theme.Colors.Select(c => c.Color).ToArray(), 10);
            Save(s => s.Appearance.Colors = theme.Name);
        };
        StartWithWindows.Toggled += async (_, _) =>
        {
            if (_loading)
                return;
            string? problem = await Autostart.SetAsync(StartWithWindows.IsOn);
            StartWithWindowsProblem.Text = problem ?? "";
            StartWithWindowsProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            if (problem is not null)
                await LoadStartWithWindowsAsync(); // show what it really is
            WelcomeWindow.Refresh();
        };
        ShowWelcome.Toggled += (_, _) =>
        {
            Save(s => s.ShowWelcome = ShowWelcome.IsOn);
            WelcomeWindow.Refresh();
        };
        ShowMagnifier.Toggled += (_, _) => Save(s => s.Capture.ShowMagnifier = ShowMagnifier.IsOn);
        CopyRightAway.Toggled += (_, _) => Save(s => s.Capture.CopyRightAway = CopyRightAway.IsOn);
        CloseAfterExport.Toggled += (_, _) => Save(s => s.Editor.CloseAfterSaveOrCopy = CloseAfterExport.IsOn);
        KeepOutOfHistory.Toggled += (_, _) => Save(s => s.Clipboard.KeepOutOfHistory = KeepOutOfHistory.IsOn);
        BeautifyByDefault.Toggled += (_, _) => Save(s => s.Beautify.OnByDefault = BeautifyByDefault.IsOn);
        BeautifyPreset.SelectionChanged += (_, _) =>
        {
            if (BeautifyPreset.SelectedIndex >= 0)
                Save(s => s.Beautify.Preset = BackdropPresets.All[BeautifyPreset.SelectedIndex].Name);
        };
        BeautifyPadding.ValueChanged += (_, e) => Save(s => s.Beautify.Padding = e.NewValue);
        SaveWithoutAsking.Toggled += (_, _) => Save(s => s.Saving.SaveWithoutAsking = SaveWithoutAsking.IsOn);
        _saveFolder = new FolderRow(this, "Save folder", SaveFolderPath, SaveFolderProblem, ChangeSaveFolder, OpenSaveFolder, ResetSaveFolder,
            Windows.Storage.Pickers.PickerLocationId.PicturesLibrary, Settings.SavingSettings.DefaultFolder,
            () => Settings.Current.Saving.Folder, folder => Settings.Current.Saving.Folder = folder, () => Settings.Current.Saving.EffectiveFolder);
        _videoFolder = new FolderRow(this, "Video folder", VideoFolderPath, VideoFolderProblem, ChangeVideoFolder, OpenVideoFolder, ResetVideoFolder,
            Windows.Storage.Pickers.PickerLocationId.VideosLibrary, Settings.VideoSettings.DefaultFolder,
            () => Settings.Current.Video.Folder, folder => Settings.Current.Video.Folder = folder, () => Settings.Current.Video.EffectiveFolder);
        VideoFrameRate.Items.Add("30 frames a second");
        VideoFrameRate.Items.Add("60 frames a second");
        VideoFrameRate.SelectionChanged += (_, _) =>
        {
            if (VideoFrameRate.SelectedIndex >= 0)
                Save(s => s.Video.FrameRate = VideoFrameRate.SelectedIndex == 1 ? 60 : 30);
        };
        VideoCursor.Toggled += (_, _) => Save(s => s.Video.Cursor = VideoCursor.IsOn);
        foreach (int seconds in Countdowns)
            VideoCountdown.Items.Add(seconds == 0 ? "None" : $"{seconds} seconds");
        VideoCountdown.SelectionChanged += (_, _) =>
        {
            if (VideoCountdown.SelectedIndex >= 0)
                Save(s => s.Video.Countdown = Countdowns[VideoCountdown.SelectedIndex]);
        };
        VideoMicrophone.Toggled += (_, _) => Save(s => s.Video.Microphone = VideoMicrophone.IsOn);
        OpenFile.Click += (_, _) => App.OpenSettingsFile();

        Load();
        SizeAndCenter();
        if (File.Exists(AppIcon.FilePath))
            AppWindow.SetIcon(AppIcon.FilePath);
        Closed += (_, _) =>
        {
            _shortcuts.Closed();
            _current = null;
        };
    }

    public static void ShowOrActivate()
    {
        _current ??= new SettingsWindow();
        _current.Activate();
    }

    /// <summary>Shows the settings again after the file was edited by hand, or in the welcome window.</summary>
    public static void Refresh() => _current?.Load();

    private void SizeAndCenter()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        int width = (int)(680 * scale), height = Math.Min((int)(860 * scale), area.Height - (int)(40 * scale));
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void Load()
    {
        _loading = true;
        var s = Settings.Current;
        _shortcuts.Load();
        _ = LoadStartWithWindowsAsync();
        ShowWelcome.IsOn = s.ShowWelcome;
        ShowMagnifier.IsOn = s.Capture.ShowMagnifier;
        CopyRightAway.IsOn = s.Capture.CopyRightAway;
        CloseAfterExport.IsOn = s.Editor.CloseAfterSaveOrCopy;
        KeepOutOfHistory.IsOn = s.Clipboard.KeepOutOfHistory;
        BeautifyByDefault.IsOn = s.Beautify.OnByDefault;
        BeautifyPreset.SelectedIndex = Math.Max(0, Array.FindIndex(BackdropPresets.All,
            p => p.Name.Equals(s.Beautify.Preset, StringComparison.OrdinalIgnoreCase)));
        BeautifyPadding.Value = Math.Clamp(s.Beautify.Padding, 16, 160);
        SaveWithoutAsking.IsOn = s.Saving.SaveWithoutAsking;
        _saveFolder.Show();
        _videoFolder.Show();
        VideoFrameRate.SelectedIndex = s.Video.FrameRate >= 60 ? 1 : 0;
        VideoCursor.IsOn = s.Video.Cursor;
        VideoCountdown.SelectedIndex = Math.Max(0, Array.IndexOf(Countdowns, Countdowns.OrderBy(c => Math.Abs(c - s.Video.Countdown)).First()));
        VideoMicrophone.IsOn = s.Video.Microphone;
        WindowTheme.SelectedIndex = Array.IndexOf(Themes.All, Themes.Find(s.Appearance.Window));
        ColorSet.SelectedIndex = Array.IndexOf(Themes.All, Themes.Find(s.Appearance.Colors));
        _loading = false;
    }

    // ---- Folders -----------------------------------------------------------------------

    private readonly FolderRow _saveFolder;
    private readonly FolderRow _videoFolder;

    /// <summary>A folder setting: its path, Change…, open it, and back to the default.</summary>
    private sealed class FolderRow
    {
        private readonly Window _window;
        private readonly string _name;
        private readonly TextBlock _path, _problem;
        private readonly Button _reset;
        private readonly Windows.Storage.Pickers.PickerLocationId _start;
        private readonly string _default;
        private readonly Func<string> _get;
        private readonly Action<string> _set;
        private readonly Func<string> _effective;

        public FolderRow(Window window, string name, TextBlock path, TextBlock problem, Button change, Button open, Button reset,
            Windows.Storage.Pickers.PickerLocationId start, string defaultFolder, Func<string> get, Action<string> set, Func<string> effective)
        {
            (_window, _name, _path, _problem, _reset, _start, _default, _get, _set, _effective) =
                (window, name, path, problem, reset, start, defaultFolder, get, set, effective);
            open.Content = FluentIcons.Create("folder_open", filled: false);
            reset.Content = FluentIcons.Create("arrow_reset", filled: false);
            change.Click += async (_, _) => await PickAsync();
            reset.Click += (_, _) => Set("");
            open.Click += (_, _) =>
            {
                string folder = _effective();
                try
                {
                    Directory.CreateDirectory(folder); // the default doesn't exist until the first save
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    ShowProblem($"Couldn't open {folder}: {ex.Message}");
                }
            };
        }

        public void Show()
        {
            _path.Text = _effective();
            _reset.Visibility = string.IsNullOrWhiteSpace(_get()) ? Visibility.Collapsed : Visibility.Visible;
            _problem.Visibility = Visibility.Collapsed;
        }

        private void ShowProblem(string text)
        {
            _problem.Text = text;
            _problem.Visibility = Visibility.Visible;
        }

        private async Task PickAsync()
        {
            var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = _start };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
            Windows.Storage.StorageFolder? picked;
            try
            {
                picked = await picker.PickSingleFolderAsync();
            }
            catch (Exception ex)
            {
                Log.Write($"Folder picker failed: {ex.Message}");
                ShowProblem("Couldn't show the folder picker. You can type a folder into the settings file instead.");
                return;
            }
            if (picked is null)
                return;
            // Libraries and other virtual folders have no path to save into.
            if (string.IsNullOrEmpty(picked.Path))
            {
                ShowProblem($"{picked.DisplayName} isn't a folder on disk. Pick a folder inside it.");
                return;
            }
            // Picking the default folder itself goes back to following Pictures or Videos (e.g. if it moves to OneDrive).
            Set(string.Equals(Path.TrimEndingDirectorySeparator(picked.Path), _default, StringComparison.OrdinalIgnoreCase) ? "" : picked.Path);
        }

        private void Set(string folder)
        {
            _set(folder);
            Settings.Save();
            Log.Write($"{_name} set to {_effective()}");
            Show();
        }
    }

    /// <summary>Shows a theme's colors beside its picker.</summary>
    internal static void ShowStrip(StackPanel strip, Windows.UI.Color[] colors, double swatchWidth)
    {
        strip.Children.Clear();
        foreach (var color in colors)
        {
            strip.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = swatchWidth,
                Height = 14,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color),
                Stroke = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                StrokeThickness = 1,
            });
        }
    }

    private async Task LoadStartWithWindowsAsync()
    {
        bool on = await Autostart.IsEnabledAsync();
        _loading = true;
        StartWithWindows.IsOn = on;
        _loading = false;
    }

    /// <summary>The countdowns on offer, in seconds.</summary>
    private static readonly int[] Countdowns = [0, 3, 5];

    private void Save(Action<Settings> change)
    {
        if (_loading)
            return;
        change(Settings.Current);
        Settings.Save();
    }
}
