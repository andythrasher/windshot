using System.Runtime.InteropServices;
using Microsoft.UI.Input;

namespace Windshot.Shell;

/// <summary>
/// Windshot's own cursors, sized and outlined to sit with Windows' arrow: open and grabbing
/// hands for panning and dragging pins (Windows' only hand is the link pointer), a rotate arrow
/// for the rotate handle, and a thin crosshair for drawing and capturing (Windows' precision
/// cross is heavier than the arrow). They come from Assets (made by tools/make-cursors.ps1) and
/// are turned into WinUI or WinForms cursors at the size for the display's scale.
/// </summary>
internal static class AppCursors
{
    private static readonly string OpenPath = Asset("hand-open.cur");
    private static readonly string GrabPath = Asset("hand-grab.cur");
    private static readonly string RotatePath = Asset("rotate.cur");
    private static readonly string CrossPath = Asset("cross.cur");
    private static readonly Dictionary<(string, int), IntPtr> Loaded = new();

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    public static InputCursor? Open(double scale) => ToInputCursor(Load(OpenPath, scale));
    public static InputCursor? Grab(double scale) => ToInputCursor(Load(GrabPath, scale));
    public static InputCursor? Rotate(double scale) => ToInputCursor(Load(RotatePath, scale));
    public static InputCursor? Cross(double scale) => ToInputCursor(Load(CrossPath, scale));

    public static System.Windows.Forms.Cursor? OpenForms(double scale) => ToFormsCursor(Load(OpenPath, scale));
    public static System.Windows.Forms.Cursor? GrabForms(double scale) => ToFormsCursor(Load(GrabPath, scale));
    public static System.Windows.Forms.Cursor? CrossForms(double scale) => ToFormsCursor(Load(CrossPath, scale));

    /// <summary>
    /// Loads (once per size; kept for the life of the app) the image closest to the cursor size
    /// at this scale: 32 px, or larger if the cursor size is turned up in Windows' accessibility
    /// settings, so these grow along with the arrow.
    /// </summary>
    private static IntPtr Load(string path, double scale)
    {
        int size = (int)Math.Round(BaseSize * scale);
        if (Loaded.TryGetValue((path, size), out var cursor))
            return cursor;
        cursor = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, size, size, LR_LOADFROMFILE);
        if (cursor == IntPtr.Zero)
            Log.Write($"Couldn't load cursor {path}");
        Loaded[(path, size)] = cursor;
        return cursor;
    }

    /// <summary>The cursor size at 100% scaling: 32 unless made larger in Settings > Accessibility > Mouse pointer and touch.</summary>
    private static int BaseSize
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
                return key?.GetValue("CursorBaseSize") is int size && size is >= 32 and <= 256 ? size : 32;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return 32;
            }
        }
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
