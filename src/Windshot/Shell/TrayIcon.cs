using System.Drawing;
using System.Windows.Forms;

namespace Windshot.Shell;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _copyTextItem;

    public TrayIcon(Action capture, Action copyText, Action openSettings, Action quit)
    {
        var menu = new ContextMenuStrip();
        _captureItem = new ToolStripMenuItem("Capture region", null, (_, _) => capture());
        _copyTextItem = new ToolStripMenuItem("Copy text from region", null, (_, _) => copyText());
        menu.Items.Add(_captureItem);
        menu.Items.Add(_copyTextItem);
        menu.Items.Add("Close all pins", null, (_, _) => PinWindow.CloseAll());
        menu.Items.Add(new ToolStripSeparator());
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
    public void SetShortcuts(string? capture, string? copyText)
    {
        _captureItem.ShortcutKeyDisplayString = capture ?? "unavailable";
        _copyTextItem.ShortcutKeyDisplayString = copyText ?? "unavailable";
        _icon.Text = capture is null ? "Windshot (capture hotkey unavailable)" : $"Windshot - {capture}";
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
