using System.Drawing;
using System.Windows.Forms;

namespace Windshot.Shell;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _copyTextItem;
    private readonly ToolStripMenuItem _scrollingItem;
    private readonly ToolStripMenuItem _recordItem;
    private readonly ToolStripMenuItem _delayItem;
    private readonly ToolStripMenuItem _cancelDelayItem;

    public TrayIcon(Action capture, Action copyText, Action scrollingCapture, Action record, Action<int> delayedCapture, Action cancelDelay,
        Action openFile, Action openSettings, Action quit)
    {
        var menu = new ContextMenuStrip();
        _captureItem = new ToolStripMenuItem("Capture region", null, (_, _) => capture());
        _scrollingItem = new ToolStripMenuItem("Scrolling capture", null, (_, _) => scrollingCapture());
        _copyTextItem = new ToolStripMenuItem("Copy text from region", null, (_, _) => copyText());
        _recordItem = new ToolStripMenuItem("Record video", null, (_, _) => record());
        // A few seconds to open a menu or hover something first; clicked while counting, it stops.
        _delayItem = new ToolStripMenuItem("Capture after a delay");
        foreach (int seconds in new[] { 3, 5, 10 })
            _delayItem.DropDownItems.Add($"{seconds} seconds", null, (_, _) => delayedCapture(seconds));
        _cancelDelayItem = new ToolStripMenuItem("Cancel delayed capture", null, (_, _) => cancelDelay()) { Visible = false };
        menu.Items.Add(_captureItem);
        menu.Items.Add(_delayItem);
        menu.Items.Add(_cancelDelayItem);
        menu.Items.Add(_scrollingItem);
        menu.Items.Add(_copyTextItem);
        menu.Items.Add(_recordItem);
        menu.Items.Add("Open image or video…", null, (_, _) => openFile());
        menu.Items.Add("Close all pins", null, (_, _) => PinWindow.CloseAll());
        menu.Items.Add(new ToolStripSeparator());
        var autostart = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        autostart.Click += async (_, _) =>
        {
            if (await Autostart.SetAsync(autostart.Checked) is { } problem)
            {
                autostart.Checked = await Autostart.IsEnabledAsync();
                _icon!.ShowBalloonTip(8000, "Start with Windows", problem, ToolTipIcon.Info);
            }
        };
        // Read fresh each time: it can also be switched off in Task Manager.
        menu.Opening += async (_, _) => autostart.Checked = await Autostart.IsEnabledAsync();
        menu.Items.Add(autostart);
        menu.Items.Add("Settings…", null, (_, _) => openSettings());
        menu.Items.Add("Quit Windshot", null, (_, _) => quit());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Windshot",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                capture();
        };
    }

    /// <param name="capture">Shortcut label, or null if it couldn't be registered.</param>
    /// <param name="copyText">Shortcut label, or null if it couldn't be registered.</param>
    /// <param name="scrolling">Shortcut label, or null if it couldn't be registered.</param>
    /// <param name="record">Shortcut label, or null if it couldn't be registered.</param>
    public void SetShortcuts(string? capture, string? copyText, string? scrolling, string? record)
    {
        _captureItem.ShortcutKeyDisplayString = capture ?? "unavailable";
        _copyTextItem.ShortcutKeyDisplayString = copyText ?? "unavailable";
        _scrollingItem.ShortcutKeyDisplayString = scrolling ?? "unavailable";
        _recordItem.ShortcutKeyDisplayString = record ?? "unavailable";
        _icon.Text = capture switch
        {
            null => "Windshot (capture hotkey unavailable)",
            "" => "Windshot",
            _ => $"Windshot - {capture}",
        };
    }

    /// <summary>While recording, the menu item (and the shortcut) stops it.</summary>
    public void SetRecording(bool recording) => _recordItem.Text = recording ? "Stop recording" : "Record video";

    /// <summary>While a delayed capture counts down, the menu offers to cancel it instead.</summary>
    public void SetDelaying(bool delaying)
    {
        _delayItem.Visible = !delaying;
        _cancelDelayItem.Visible = delaying;
    }

    /// <summary>The app icon at the tray's size for this DPI (e.g. 24 px at 150%), so it stays crisp.</summary>
    private static Icon LoadIcon()
    {
        try
        {
            return new Icon(AppIcon.FilePath, SystemInformation.SmallIconSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            Log.Write($"Couldn't load the tray icon: {ex.Message}");
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
