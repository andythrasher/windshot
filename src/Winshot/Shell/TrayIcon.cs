using System.Drawing;
using System.Windows.Forms;

namespace Winshot.Shell;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action capture, Action quit, string? hotkeyLabel)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(hotkeyLabel is null ? "Capture region" : $"Capture region ({hotkeyLabel})", null, (_, _) => capture());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Winshot", null, (_, _) => quit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = hotkeyLabel is null ? "Winshot (hotkey unavailable)" : $"Winshot - {hotkeyLabel}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                capture();
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
