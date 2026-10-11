using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Windshot.Shell;

/// <summary>
/// The three shortcut rows shared by the settings and welcome windows. Shortcuts are recorded
/// by clicking one and pressing the new keys; Windshot's hotkeys are let go of meanwhile, so
/// the current combination can be pressed without starting a capture.
/// </summary>
internal sealed class ShortcutEditor
{
    /// <summary>One shortcut: its button, reset button and the line that explains a problem.</summary>
    public sealed record Row(string Name, Button Button, Button Reset, TextBlock Problem,
        Func<Settings.HotkeySettings, string> Get, Action<Settings.HotkeySettings, string> Set, string Default,
        Func<App.HotkeyStatus, bool> Registered);

    private readonly Row[] _rows;
    private readonly Action _changed;
    private Row? _recording;

    /// <param name="changed">Called after a shortcut is saved, e.g. to update the other window.</param>
    public ShortcutEditor(Button capture, Button captureReset, TextBlock captureProblem,
        Button scrolling, Button scrollingReset, TextBlock scrollingProblem,
        Button copyText, Button copyTextReset, TextBlock copyTextProblem, Action changed)
    {
        _changed = changed;
        var defaults = new Settings.HotkeySettings();
        _rows =
        [
            new("Capture", capture, captureReset, captureProblem, h => h.Capture, (h, v) => h.Capture = v, defaults.Capture, s => s.Capture),
            new("Scrolling capture", scrolling, scrollingReset, scrollingProblem, h => h.ScrollingCapture, (h, v) => h.ScrollingCapture = v, defaults.ScrollingCapture, s => s.ScrollingCapture),
            new("Copy text", copyText, copyTextReset, copyTextProblem, h => h.CopyText, (h, v) => h.CopyText = v, defaults.CopyText, s => s.CopyText),
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
    }

    /// <summary>Shows the saved shortcuts, and flags any that aren't working.</summary>
    public void Load()
    {
        foreach (var row in _rows)
            row.Button.Content = Display(row.Get(Settings.Current.Hotkeys));
        ShowStatus(App.Instance.ApplyHotkeys());
    }

    /// <summary>When the window closes mid-recording, take the hotkeys back.</summary>
    public void Closed()
    {
        if (_recording is not null)
            App.Instance.ApplyHotkeys();
        _recording = null;
    }

    public static string Display(string shortcut) => string.IsNullOrWhiteSpace(shortcut) ? "Off" : shortcut.Replace("+", " + ");

    private void StartRecording(Row row)
    {
        if (_recording is not null && _recording != row)
            StopRecording(_recording, newShortcut: null);
        _recording = row;
        App.Instance.SuspendHotkeys();
        row.Button.Content = "Press a shortcut…";
        row.Problem.Visibility = Visibility.Collapsed;
    }

    /// <param name="newShortcut">What was pressed ("" turns it off), or null to keep the old one.</param>
    private void StopRecording(Row row, string? newShortcut)
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

    private void RecordKey(Row row, KeyRoutedEventArgs e)
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
    private void Change(Row row, string shortcut)
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
            ShowProblem(row, IsPrintScreen(shortcut) ? PrintScreenTaken : $"{Display(shortcut)} is in use by another app or by Windows. Try another.");
            row.Button.Content = Display(previous);
            ShowStatus(status, except: row);
            return;
        }

        Settings.Save();
        row.Button.Content = Display(shortcut);
        Log.Write($"{row.Name} shortcut set to {Display(shortcut)}");
        ShowStatus(status);
        _changed();
    }

    private static bool Same(string a, string b) =>
        HotkeyWindow.TryParse(a, out var ma, out var ka) && HotkeyWindow.TryParse(b, out var mb, out var kb) && ma == mb && ka == kb;

    private static void ShowProblem(Row row, string text)
    {
        row.Problem.Text = text;
        row.Problem.Visibility = Visibility.Visible;
    }

    /// <summary>Flags shortcuts that couldn't be registered (e.g. set by hand to one another app owns).</summary>
    private void ShowStatus(App.HotkeyStatus status, Row? except = null)
    {
        foreach (var row in _rows)
        {
            if (row == except)
                continue;
            if (row.Registered(status))
                row.Problem.Visibility = Visibility.Collapsed;
            else if (IsPrintScreen(row.Get(Settings.Current.Hotkeys)))
                ShowProblem(row, PrintScreenTaken);
            else
                ShowProblem(row, $"{Display(row.Get(Settings.Current.Hotkeys))} isn't working: another app or Windows is using it.");
        }
    }

    /// <summary>Windows 11 gives the bare Print Screen key to Snipping Tool unless that's turned off.</summary>
    private const string PrintScreenTaken =
        "Windows is using Print Screen for Snipping Tool. To use it here, turn off \"Use the Print screen key to open screen capture\" in Settings > Accessibility > Keyboard, then pick it again.";

    private static bool IsPrintScreen(string shortcut) =>
        HotkeyWindow.TryParse(shortcut, out var modifiers, out var key) && modifiers == HotkeyModifiers.None && key == System.Windows.Forms.Keys.PrintScreen;
}
