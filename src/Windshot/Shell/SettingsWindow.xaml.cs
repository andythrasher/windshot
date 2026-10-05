using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using Windshot.Editing;

namespace Windshot.Shell;

/// <summary>
/// The settings window (tray menu → Settings…). Every change is saved and applied at once.
/// Shortcuts are recorded by clicking one and pressing the new keys; Windshot's hotkeys are
/// let go of meanwhile, so the current combination can be pressed without starting a capture.
/// Anything not shown here is in settings.json, linked at the bottom.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private static SettingsWindow? _current;

    /// <summary>One of the three shortcut rows.</summary>
    private sealed record ShortcutRow(string Name, Button Button, Button Reset, TextBlock Problem,
        Func<Settings.HotkeySettings, string> Get, Action<Settings.HotkeySettings, string> Set, string Default,
        Func<App.HotkeyStatus, bool> Registered);

    private readonly ShortcutRow[] _rows;
    private ShortcutRow? _recording;
    /// <summary>Set while showing values, so the controls' change events don't save them straight back.</summary>
    private bool _loading;

    private SettingsWindow()
    {
        InitializeComponent();
        var defaults = new Settings.HotkeySettings();
        _rows =
        [
            new("Capture", CaptureShortcut, CaptureReset, CaptureProblem, h => h.Capture, (h, v) => h.Capture = v, defaults.Capture, s => s.Capture),
            new("Scrolling capture", ScrollingShortcut, ScrollingReset, ScrollingProblem, h => h.ScrollingCapture, (h, v) => h.ScrollingCapture = v, defaults.ScrollingCapture, s => s.ScrollingCapture),
            new("Copy text", CopyTextShortcut, CopyTextReset, CopyTextProblem, h => h.CopyText, (h, v) => h.CopyText = v, defaults.CopyText, s => s.CopyText),
        ];
        foreach (var row in _rows)
        {
            row.Button.Click += (_, _) => StartRecording(row);
            // Caught on the way down, so Enter, Space and Tab are recorded instead of acting on the button.
            row.Button.PreviewKeyDown += (_, e) => RecordKey(row, e);
            // Print Screen only reports key-up.
            row.Button.PreviewKeyUp += (_, e) =>
            {
                if (_recording == row && e.Key == VirtualKey.Snapshot)
                    RecordKey(row, e);
            };
            row.Button.LostFocus += (_, _) => StopRecording(row, newShortcut: null);
            row.Reset.Content = FluentIcons.Create("arrow_reset", filled: false);
            row.Reset.Click += (_, _) => Change(row, row.Default);
        }

        BeautifyPreset.ItemsSource = BackdropPresets.All.Select(p => p.Name).ToList();
        StartWithWindows.Toggled += (_, _) => { if (!_loading) Autostart.Set(StartWithWindows.IsOn); };
        ShowMagnifier.Toggled += (_, _) => Save(s => s.Capture.ShowMagnifier = ShowMagnifier.IsOn);
        CloseAfterExport.Toggled += (_, _) => Save(s => s.Editor.CloseAfterSaveOrCopy = CloseAfterExport.IsOn);
        KeepOutOfHistory.Toggled += (_, _) => Save(s => s.Clipboard.KeepOutOfHistory = KeepOutOfHistory.IsOn);
        BeautifyByDefault.Toggled += (_, _) => Save(s => s.Beautify.OnByDefault = BeautifyByDefault.IsOn);
        BeautifyPreset.SelectionChanged += (_, _) =>
        {
            if (BeautifyPreset.SelectedItem is string preset)
                Save(s => s.Beautify.Preset = preset);
        };
        BeautifyPadding.ValueChanged += (_, e) => Save(s => s.Beautify.Padding = e.NewValue);
        OpenFile.Click += (_, _) => App.OpenSettingsFile();

        Load();
        ShowStatus(App.Instance.ApplyHotkeys());
        SizeAndCenter();
        if (File.Exists(AppIcon.FilePath))
            AppWindow.SetIcon(AppIcon.FilePath);
        Closed += (_, _) =>
        {
            if (_recording is not null)
                App.Instance.ApplyHotkeys(); // take the hotkeys back
            _current = null;
        };
    }

    public static void ShowOrActivate()
    {
        _current ??= new SettingsWindow();
        _current.Activate();
    }

    /// <summary>Shows the settings again after the file was edited by hand.</summary>
    public static void Refresh()
    {
        if (_current is { } window)
        {
            window.Load();
            window.ShowStatus(App.Instance.ApplyHotkeys());
        }
    }

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
        foreach (var row in _rows)
            row.Button.Content = Display(row.Get(s.Hotkeys));
        StartWithWindows.IsOn = Autostart.IsEnabled;
        ShowMagnifier.IsOn = s.Capture.ShowMagnifier;
        CloseAfterExport.IsOn = s.Editor.CloseAfterSaveOrCopy;
        KeepOutOfHistory.IsOn = s.Clipboard.KeepOutOfHistory;
        BeautifyByDefault.IsOn = s.Beautify.OnByDefault;
        BeautifyPreset.SelectedItem = BackdropPresets.All.FirstOrDefault(p => p.Name.Equals(s.Beautify.Preset, StringComparison.OrdinalIgnoreCase)).Name
            ?? BackdropPresets.All[0].Name;
        BeautifyPadding.Value = Math.Clamp(s.Beautify.Padding, 16, 160);
        _loading = false;
    }

    private void Save(Action<Settings> change)
    {
        if (_loading)
            return;
        change(Settings.Current);
        Settings.Save();
    }

    private static string Display(string shortcut) => string.IsNullOrWhiteSpace(shortcut) ? "Off" : shortcut.Replace("+", " + ");

    // ---- Recording shortcuts -----------------------------------------------------------

    private void StartRecording(ShortcutRow row)
    {
        if (_recording is not null && _recording != row)
            StopRecording(_recording, newShortcut: null);
        _recording = row;
        App.Instance.SuspendHotkeys();
        row.Button.Content = "Press a shortcut…";
        row.Problem.Visibility = Visibility.Collapsed;
    }

    /// <param name="newShortcut">What was pressed ("" turns it off), or null to keep the old one.</param>
    private void StopRecording(ShortcutRow row, string? newShortcut)
    {
        if (_recording != row)
            return;
        _recording = null;
        if (newShortcut is null)
        {
            row.Button.Content = Display(row.Get(Settings.Current.Hotkeys));
            ShowStatus(App.Instance.ApplyHotkeys());
        }
        else
        {
            Change(row, newShortcut);
        }
    }

    private void RecordKey(ShortcutRow row, KeyRoutedEventArgs e)
    {
        if (_recording != row)
            return;
        e.Handled = true;
        var key = e.Key;
        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            row.Button.Content = Describe(Modifiers(), null) + "…"; // show what's held so far
            return;
        }

        var modifiers = Modifiers();
        if (key == VirtualKey.Escape && modifiers.Count == 0)
        {
            StopRecording(row, newShortcut: null);
            return;
        }
        if (key is VirtualKey.Back or VirtualKey.Delete && modifiers.Count == 0)
        {
            StopRecording(row, newShortcut: "");
            return;
        }

        // On its own (or with just Shift), a key would stop working for typing everywhere.
        bool standalone = key is >= VirtualKey.F1 and <= VirtualKey.F24 or VirtualKey.Snapshot or VirtualKey.Pause;
        if (!standalone && !modifiers.Any(m => m is "Ctrl" or "Alt" or "Win"))
        {
            row.Problem.Text = "Add Ctrl, Alt or Win, so the shortcut doesn't get in the way of typing.";
            row.Problem.Visibility = Visibility.Visible;
            row.Button.Content = "Press a shortcut…";
            return;
        }
        StopRecording(row, Describe(modifiers, key));
    }

    private static List<string> Modifiers()
    {
        static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);
        var list = new List<string>();
        if (Down(VirtualKey.Control)) list.Add("Ctrl");
        if (Down(VirtualKey.Menu)) list.Add("Alt");
        if (Down(VirtualKey.Shift)) list.Add("Shift");
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) list.Add("Win");
        return list;
    }

    /// <summary>Written the way settings.json and the hotkey parser expect, e.g. "Ctrl+Shift+2".</summary>
    private static string Describe(List<string> modifiers, VirtualKey? key)
    {
        var parts = new List<string>(modifiers);
        if (key is VirtualKey k)
        {
            parts.Add(k switch
            {
                >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (k - VirtualKey.Number0))).ToString(),
                VirtualKey.Snapshot => "PrintScreen",
                // Virtual-key codes are the same numbers as WinForms' Keys, whose names the parser reads.
                _ => ((System.Windows.Forms.Keys)(int)k).ToString(),
            });
        }
        return string.Join("+", parts);
    }

    /// <summary>Saves and applies a shortcut, putting the old one back if it can't be used.</summary>
    private void Change(ShortcutRow row, string shortcut)
    {
        var hotkeys = Settings.Current.Hotkeys;
        string previous = row.Get(hotkeys);
        row.Problem.Visibility = Visibility.Collapsed;

        if (shortcut != "" && _rows.FirstOrDefault(r => r != row && Same(r.Get(hotkeys), shortcut)) is { } other)
        {
            ShowProblem(row, $"{Display(shortcut)} is already the {other.Name.ToLowerInvariant()} shortcut.");
            row.Button.Content = Display(previous);
            ShowStatus(App.Instance.ApplyHotkeys(), except: row);
            return;
        }

        row.Set(hotkeys, shortcut);
        var status = App.Instance.ApplyHotkeys();
        if (!row.Registered(status))
        {
            // Taken by another app (or Windows): keep the old one.
            row.Set(hotkeys, previous);
            status = App.Instance.ApplyHotkeys();
            ShowProblem(row, $"{Display(shortcut)} is in use by another app or by Windows. Try another.");
            row.Button.Content = Display(previous);
            ShowStatus(status, except: row);
            return;
        }

        Settings.Save();
        row.Button.Content = Display(shortcut);
        Log.Write($"{row.Name} shortcut set to {Display(shortcut)}");
        ShowStatus(status);
    }

    private static bool Same(string a, string b) =>
        HotkeyWindow.TryParse(a, out var ma, out var ka) && HotkeyWindow.TryParse(b, out var mb, out var kb) && ma == mb && ka == kb;

    private static void ShowProblem(ShortcutRow row, string text)
    {
        row.Problem.Text = text;
        row.Problem.Visibility = Visibility.Visible;
    }

    /// <summary>Flags shortcuts that couldn't be registered (e.g. set by hand to one another app owns).</summary>
    private void ShowStatus(App.HotkeyStatus status, ShortcutRow? except = null)
    {
        foreach (var row in _rows)
        {
            if (row == except)
                continue;
            if (row.Registered(status))
                row.Problem.Visibility = Visibility.Collapsed;
            else
                ShowProblem(row, $"{Display(row.Get(Settings.Current.Hotkeys))} isn't working: another app or Windows is using it.");
        }
    }
}
