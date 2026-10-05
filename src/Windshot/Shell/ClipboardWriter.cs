using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Windshot.Shell;

/// <summary>
/// Every copy goes through here. Another app holding the clipboard open (clipboard managers,
/// remote desktop) makes a write fail now and then, so it's retried briefly; a copy that still
/// fails is reported rather than taking the app down. Honors "keep copies out of clipboard
/// history" (Win+V, and cloud clipboard sync).
/// </summary>
internal static class ClipboardWriter
{
    private const int Attempts = 5;

    /// <returns>False if the clipboard couldn't be written; the reason is logged.</returns>
    public static bool TrySet(DataPackage package)
    {
        bool keepOut = Settings.Current.Clipboard.KeepOutOfHistory;
        var options = new ClipboardContentOptions { IsAllowedInHistory = !keepOut, IsRoamable = !keepOut };
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!Clipboard.SetContentWithOptions(package, options))
                    throw new COMException("The clipboard refused the content.");
                Clipboard.Flush();
                return true;
            }
            catch (Exception ex) when (attempt < Attempts && ex is COMException or UnauthorizedAccessException)
            {
                Thread.Sleep(40 * attempt);
            }
            catch (Exception ex)
            {
                Log.Write($"Couldn't write to the clipboard: {ex.Message}");
                return false;
            }
        }
    }

    public static bool TrySetText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        return TrySet(package);
    }

    /// <summary>
    /// An image as both the standard bitmap format and PNG: the bitmap format drops
    /// transparency (rounded window corners, unfilled canvas), and most apps that paste
    /// images look for PNG first and keep it.
    /// </summary>
    public static bool TrySetPng(IRandomAccessStream png)
    {
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(png));
        package.SetData("PNG", png.CloneStream());
        return TrySet(package);
    }

    public static bool TrySetImage(System.Drawing.Bitmap image)
    {
        using var encoded = new MemoryStream();
        image.Save(encoded, System.Drawing.Imaging.ImageFormat.Png);
        var png = new InMemoryRandomAccessStream();
        // In-memory, so this completes at once; waiting on it doesn't stall the UI.
        png.WriteAsync(encoded.ToArray().AsBuffer()).AsTask().GetAwaiter().GetResult();
        png.Seek(0);
        return TrySetPng(png);
    }
}
