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

    /// <summary>Registers a shortcut written like "Ctrl+Shift+2", "Alt+S" or "PrintScreen".</summary>
    /// <returns>False if the text isn't a valid shortcut or another app already owns it.</returns>
    public bool TryRegister(string shortcut, Action handler) =>
        TryParse(shortcut, out var modifiers, out var key) && TryRegister(modifiers, key, handler);

    public bool TryRegister(HotkeyModifiers modifiers, Keys key, Action handler)
    {
        int id = _nextId++;
        if (!RegisterHotKey(Handle, id, (uint)(modifiers | HotkeyModifiers.NoRepeat), (uint)key))
            return false;

        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _handlers.Keys)
            UnregisterHotKey(Handle, id);
        _handlers.Clear();
    }

    public static bool TryParse(string? shortcut, out HotkeyModifiers modifiers, out Keys key)
    {
        modifiers = HotkeyModifiers.None;
        key = Keys.None;
        if (string.IsNullOrWhiteSpace(shortcut))
            return false;

        foreach (string part in shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "win" or "windows": modifiers |= HotkeyModifiers.Win; break;
                default:
                    if (key != Keys.None)
                        return false; // only one non-modifier key
                    key = ParseKey(part);
                    if (key == Keys.None)
                        return false;
                    break;
            }
        }
        return key != Keys.None;
    }

    private static Keys ParseKey(string text) => text.ToLowerInvariant() switch
    {
        "prtsc" or "prtscn" or "print" or "printscreen" => Keys.PrintScreen,
        // A bare digit means the number key, not the enum's numeric value.
        [var c] when char.IsAsciiDigit(c) => Keys.D0 + (c - '0'),
        _ => Enum.TryParse<Keys>(text, ignoreCase: true, out var key) && !char.IsAsciiDigit(text[0]) ? key : Keys.None,
    };

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
        UnregisterAll();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
