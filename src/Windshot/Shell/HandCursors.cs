using System.Runtime.InteropServices;
using Microsoft.UI.Input;

namespace Windshot.Shell;

/// <summary>
/// Open-hand and grabbing cursors for panning and dragging pins. Windows' only built-in hand
/// is the link pointer, so these come from Assets (made by tools/make-cursors.ps1) and are
/// turned into WinUI or WinForms cursors at the size for the display's scale.
/// </summary>
internal static class HandCursors
{
    private static readonly string OpenPath = Path.Combine(AppContext.BaseDirectory, "Assets", "hand-open.cur");
    private static readonly string GrabPath = Path.Combine(AppContext.BaseDirectory, "Assets", "hand-grab.cur");
    private static readonly Dictionary<(string, int), IntPtr> Loaded = new();

    public static InputCursor? Open(double scale) => ToInputCursor(Load(OpenPath, scale));
    public static InputCursor? Grab(double scale) => ToInputCursor(Load(GrabPath, scale));

    public static System.Windows.Forms.Cursor? OpenForms(double scale) => ToFormsCursor(Load(OpenPath, scale));
    public static System.Windows.Forms.Cursor? GrabForms(double scale) => ToFormsCursor(Load(GrabPath, scale));

    /// <summary>Loads (once per size; kept for the life of the app) the image closest to 32 px at this scale.</summary>
    private static IntPtr Load(string path, double scale)
    {
        int size = (int)Math.Round(32 * scale);
        if (Loaded.TryGetValue((path, size), out var cursor))
            return cursor;
        cursor = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, size, size, LR_LOADFROMFILE);
        if (cursor == IntPtr.Zero)
            Log.Write($"Couldn't load cursor {path}");
        Loaded[(path, size)] = cursor;
        return cursor;
    }

    private static System.Windows.Forms.Cursor? ToFormsCursor(IntPtr cursor) =>
        cursor == IntPtr.Zero ? null : new System.Windows.Forms.Cursor(cursor);

    /// <summary>Wraps a Win32 cursor for WinUI, through the interop interface on InputCursor's factory.</summary>
    private static InputCursor? ToInputCursor(IntPtr cursor)
    {
        if (cursor == IntPtr.Zero)
            return null;
        try
        {
            var factory = WinRT.ActivationFactory.Get("Microsoft.UI.Input.InputCursor");
            var iid = new Guid("ac6f5065-90c4-46ce-beb7-05e138e54117"); // IInputCursorStaticsInterop
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in iid, out IntPtr interop));
            try
            {
                // IInspectable's six methods come first; CreateFromHCursor is the seventh.
                var create = Marshal.GetDelegateForFunctionPointer<CreateFromHCursor>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(interop), 6 * IntPtr.Size));
                Marshal.ThrowExceptionForHR(create(interop, cursor, out IntPtr result));
                var input = InputCursor.FromAbi(result);
                Marshal.Release(result);
                return input;
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't make a WinUI cursor: {ex.Message}");
            return null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateFromHCursor(IntPtr self, IntPtr cursor, out IntPtr result);

    private const uint IMAGE_CURSOR = 2;
    private const uint LR_LOADFROMFILE = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
}
