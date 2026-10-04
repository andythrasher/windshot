using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Winshot.Shell;

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
    NoRepeat = 0x4000,
}

/// <summary>Hidden window that receives WM_HOTKEY for globally registered shortcuts.</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public bool TryRegister(HotkeyModifiers modifiers, Keys key, Action handler)
    {
        int id = _nextId++;
        if (!RegisterHotKey(Handle, id, (uint)(modifiers | HotkeyModifiers.NoRepeat), (uint)key))
            return false;

        _handlers[id] = handler;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _handlers.TryGetValue((int)m.WParam, out var handler))
        {
            handler();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (int id in _handlers.Keys)
            UnregisterHotKey(Handle, id);
        _handlers.Clear();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
