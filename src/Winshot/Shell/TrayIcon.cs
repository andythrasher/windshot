using System.Drawing;
using System.Windows.Forms;

namespace Winshot.Shell;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    /// <param name="captureHotkey">Shortcut label, or null if the hotkey couldn't be registered.</param>
    /// <param name="copyTextHotkey">Shortcut label, or null if the hotkey couldn't be registered.</param>
    public TrayIcon(Action capture, Action copyText, Action quit, string? captureHotkey, string? copyTextHotkey)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(WithShortcut("Capture region", captureHotkey), null, (_, _) => capture());
        menu.Items.Add(WithShortcut("Copy text from region", copyTextHotkey), null, (_, _) => copyText());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Winshot", null, (_, _) => quit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = captureHotkey is null ? "Winshot (hotkey unavailable)" : $"Winshot - {captureHotkey}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                capture();
        };
    }

    private static string WithShortcut(string label, string? shortcut) =>
        shortcut is null ? label : $"{label} ({shortcut})";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
