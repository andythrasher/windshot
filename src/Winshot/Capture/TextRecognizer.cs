using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Winshot.Capture;

/// <summary>Reads text from pixels with Windows' built-in, offline OCR engine.</summary>
internal static class TextRecognizer
{
    /// <summary>Recognizes text and puts it on the clipboard.</summary>
    /// <returns>A short message for the user describing what happened.</returns>
    public static async Task<string> CopyToClipboardAsync(byte[] bgra, int width, int height, double scale)
    {
        try
        {
            string text = await RecognizeAsync(bgra, width, height, scale);
            if (string.IsNullOrWhiteSpace(text))
                return "No text found";

            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();

            int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            return words == 1 ? "Copied 1 word" : $"Copied {words} words";
        }
        catch (NoOcrLanguageException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            Log.Write($"OCR failed: {ex}");
            return "Couldn't read text";
        }
    }

    public static async Task<string> RecognizeAsync(byte[] bgra, int width, int height, double scale)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new NoOcrLanguageException();

        // The engine misses small text, so give low-DPI captures (small UI text) twice the
        // pixels when that still fits within its size limit; shrink anything too large.
        int max = (int)OcrEngine.MaxImageDimension;
        double factor = scale < 1.5 && Math.Max(width, height) * 2 <= max ? 2
            : Math.Max(width, height) > max ? (double)max / Math.Max(width, height)
            : 1;
        if (factor != 1)
            (bgra, width, height) = Resize(bgra, width, height, factor);

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
    }

    private static (byte[] Pixels, int Width, int Height) Resize(byte[] bgra, int width, int height, double factor)
    {
        int newWidth = Math.Max(1, (int)Math.Round(width * factor));
        int newHeight = Math.Max(1, (int)Math.Round(height * factor));

        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var source = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            using var target = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(target))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, 0, 0, newWidth, newHeight);
            }

            var data = target.LockBits(new Rectangle(0, 0, newWidth, newHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[newWidth * newHeight * 4];
                for (int y = 0; y < newHeight; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * newWidth * 4, newWidth * 4);
                return (pixels, newWidth, newHeight);
            }
            finally
            {
                target.UnlockBits(data);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private sealed class NoOcrLanguageException()
        : Exception("No OCR language installed. Add one in Settings > Time & language > Language & region.");
}
