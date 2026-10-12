using System.Drawing;
using Microsoft.Graphics.Canvas;
using Windshot.Video;

namespace Windshot.Capture;

/// <summary>
/// An image or video from disk, opened in the editor like a capture: from the tray's "Open
/// image or video", Explorer's "Open with", or a path given on the command line. Videos are
/// edited where they are and never deleted; saving makes a new file.
/// </summary>
internal static class OpenedFile
{
    public static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];
    public static readonly string[] VideoTypes = [".mp4", ".mov", ".m4v", ".wmv", ".avi"];

    public static bool CanOpen(string path) =>
        ImageTypes.Contains(Path.GetExtension(path).ToLowerInvariant()) || VideoTypes.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The file, ready for the editor; throws (with a message for the user) if it can't be read.</summary>
    public static async Task<CapturedImage> LoadAsync(string path)
    {
        // Sized as if captured on the main monitor, so annotation sizes look the same as usual.
        var work = Monitors.Primary?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        double scale = SelectionOverlay.GetMonitorScale(work);
        bool video = VideoTypes.Contains(Path.GetExtension(path).ToLowerInvariant());
        if (video)
            return await Task.Run(() => LoadVideo(path, work, scale));

        CanvasBitmap bitmap;
        try
        {
            bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), path, 96);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Couldn't open {Path.GetFileName(path)}: it isn't a picture Windows can read, or it's too big.", ex);
        }
        using (bitmap)
        {
            var size = bitmap.SizeInPixels;
            return new CapturedImage(bitmap.GetPixelBytes(), (int)size.Width, (int)size.Height, scale, Centered(work, (int)size.Width, (int)size.Height)) { SourcePath = path };
        }
    }

    private static CapturedImage LoadVideo(string path, Rectangle work, double scale)
    {
        (byte[] Data, TimeSpan Time) first;
        int width, height;
        TimeSpan length;
        double rate;
        try
        {
            using var reader = new MediaReader(path, audio: false);
            first = reader.Read() ?? throw new InvalidDataException("The video has no frames");
            (width, height, length, rate) = (reader.Width, reader.Height, reader.Duration, reader.FrameRate);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Couldn't open {Path.GetFileName(path)}: it isn't a video Windows can play.", ex);
        }
        bool hasAudio;
        try
        {
            using var audio = new MediaReader(path, audio: true);
            hasAudio = true;
        }
        catch (Exception)
        {
            hasAudio = false; // no sound track, or one Windows can't decode
        }
        // The editor works in frames; most videos are 24 to 60 of them a second.
        int fps = rate >= 1 ? (int)Math.Round(rate) : 30;
        var video = new RecordedVideo(path, length > TimeSpan.Zero ? length : TimeSpan.FromSeconds(1), hasAudio, fps, Temporary: false);
        return new CapturedImage(first.Data, width, height, scale, Centered(work, width, height)) { Video = video };
    }

    /// <summary>Where the editor treats the file as being: the middle of the main monitor.</summary>
    private static Rectangle Centered(Rectangle work, int width, int height)
    {
        int w = Math.Min(width, work.Width), h = Math.Min(height, work.Height);
        return new Rectangle(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h);
    }
}
