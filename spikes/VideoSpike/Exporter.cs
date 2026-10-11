using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace VideoSpike;

/// <summary>
/// The export path: read the recording frame by frame, keep the trimmed range, draw an
/// annotation that changes with time on every frame (standing in for the editor's layers),
/// and write it as MP4 (with the audio) and as GIF.
/// </summary>
internal static class Exporter
{
    public static async Task<string> ExportAsync(string input, string mp4Path, string gifPath, TimeSpan trimStart, TimeSpan trimEnd, int fps, int gifWidth, int gifFps)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var video = new MediaReader(input, audio: false);
        using var audio = new MediaReader(input, audio: true);
        int width = video.Width, height = video.Height;
        var report = $"source {width}x{height}, stride {video.Stride}, audio {audio.SampleRate} Hz {audio.Channels} ch; ";

        // Audio: everything inside the trim, shifted to start at zero, fed alongside.
        var audioChunks = new BlockingCollection<(byte[] Pcm, TimeSpan Time, TimeSpan Duration)>();
        var audioTask = Task.Run(() =>
        {
            int frameBytes = audio.Channels * 2;
            long first = (long)(trimStart.TotalSeconds * audio.SampleRate), last = (long)(trimEnd.TotalSeconds * audio.SampleRate);
            audio.Seek(trimStart);
            while (audio.Read() is { } chunk)
            {
                long at = (long)Math.Round(chunk.Time.TotalSeconds * audio.SampleRate);
                long frames = chunk.Data.Length / frameBytes;
                long from = Math.Max(at, first), to = Math.Min(at + frames, last);
                if (to <= from)
                {
                    if (at >= last)
                        break;
                    continue;
                }
                var pcm = chunk.Data.AsSpan((int)((from - at) * frameBytes), (int)((to - from) * frameBytes)).ToArray();
                audioChunks.Add((pcm, TimeSpan.FromTicks((from - first) * 10_000_000 / audio.SampleRate), TimeSpan.FromTicks((to - from) * 10_000_000 / audio.SampleRate)));
            }
            audioChunks.CompleteAdding();
        });

        var writer = new Mp4Writer(width, height, fps, audioChunks, audio.SampleRate, audio.Channels);
        await writer.StartAsync(mp4Path);

        // GIF, at a lower frame rate and size.
        int gifHeight = (int)Math.Round(height * (double)gifWidth / width / 2) * 2;
        var gifTarget = new CanvasRenderTarget(device, gifWidth, gifHeight, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Ignore);
        var gifFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(gifPath)!);
        var gifFile = await gifFolder.CreateFileAsync(Path.GetFileName(gifPath), CreationCollisionOption.ReplaceExisting);
        using var gifStream = await gifFile.OpenAsync(FileAccessMode.ReadWrite);
        var gif = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, gifStream);
        await gif.BitmapContainerProperties.SetPropertiesAsync(new BitmapPropertySet
        {
            // Loop forever (the NETSCAPE2.0 extension).
            ["/appext/Application"] = new BitmapTypedValue("NETSCAPE2.0"u8.ToArray(), Windows.Foundation.PropertyType.UInt8Array),
            ["/appext/Data"] = new BitmapTypedValue(new byte[] { 3, 1, 0, 0, 0 }, Windows.Foundation.PropertyType.UInt8Array),
        });
        int gifFrames = 0;
        TimeSpan nextGif = TimeSpan.Zero, gifStep = TimeSpan.FromSeconds(1.0 / gifFps);
        int delayCentiseconds = 0;
        (byte[] Pixels, TimeSpan Time)? pendingGif = null;

        var free = new BlockingCollection<CanvasRenderTarget>();
        for (int i = 0; i < 16; i++)
            free.Add(new CanvasRenderTarget(device, width, height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Ignore));

        int read = 0, written = 0;
        var readTime = TimeSpan.Zero;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        video.Seek(trimStart);
        while (true)
        {
            var t0 = clock.Elapsed;
            var next = video.Read();
            readTime += clock.Elapsed - t0;
            if (next is not { } frame || frame.Time >= trimEnd)
                break;
            read++;
            if (frame.Time < trimStart)
                continue; // decoded only to get from the keyframe to the exact start
            var time = frame.Time - trimStart;

            if (!free.TryTake(out var target, 10_000))
                throw new TimeoutException($"Stalled after {written} frames: the encoder kept every render target (video samples {writer.VideoSamples}, processed {writer.Processed}, audio {writer.AudioSamples})");
            using (var bitmap = CanvasBitmap.CreateFromBytes(device, frame.Data, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore))
            using (var ds = target.CreateDrawingSession())
            {
                ds.DrawImage(bitmap);
                DrawAnnotation(ds, width, height, time, trimEnd - trimStart);
            }

            if (time >= nextGif)
            {
                using (var ds = gifTarget.CreateDrawingSession())
                    ds.DrawImage(target, new Rect(0, 0, gifWidth, gifHeight), new Rect(0, 0, width, height), 1, CanvasImageInterpolation.HighQualityCubic);
                if (pendingGif is { } previous)
                {
                    delayCentiseconds = await AddGifFrameAsync(gif, previous.Pixels, gifWidth, gifHeight, time - previous.Time, gifFrames > 0);
                    gifFrames++;
                }
                pendingGif = (gifTarget.GetPixelBytes(), time);
                while (nextGif <= time)
                    nextGif += gifStep;
            }

            writer.AddFrame(target, time, () => free.Add(target), wait: true);
            written++;
        }
        if (pendingGif is { } last)
        {
            await AddGifFrameAsync(gif, last.Pixels, gifWidth, gifHeight, gifStep, gifFrames > 0);
            gifFrames++;
        }
        await gif.FlushAsync();
        await audioTask;
        await writer.FinishAsync();
        report += $"decoded {read} frames ({read / Math.Max(0.001, readTime.TotalSeconds):F0} fps decode-only), wrote {written} in {clock.Elapsed.TotalSeconds:F1}s ({written / clock.Elapsed.TotalSeconds:F0} fps), audio samples {writer.AudioSamples}; GIF {gifFrames} frames {gifWidth}x{gifHeight}, last delay {delayCentiseconds} cs";
        return report;
    }

    /// <summary>A box that slides across, a label with the time, and a caption that fades in: proof that drawing follows the clock.</summary>
    private static void DrawAnnotation(CanvasDrawingSession ds, int width, int height, TimeSpan time, TimeSpan length)
    {
        float t = (float)(time.TotalSeconds / Math.Max(0.001, length.TotalSeconds));
        float unit = height / 1080f;
        var box = new Rect(40 * unit + t * (width - 400 * unit), height * 0.3, 320 * unit, 180 * unit);
        ds.DrawRoundedRectangle(box, 12 * unit, 12 * unit, Color.FromArgb(255, 230, 50, 50), 8 * unit);
        ds.DrawText($"{time.TotalSeconds:F2} s", new Vector2((float)box.X + 20 * unit, (float)box.Y + 20 * unit), Colors.White,
            new CanvasTextFormat { FontSize = 48 * unit, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        float fade = Math.Clamp((float)time.TotalSeconds / 1.5f, 0, 1);
        ds.FillRoundedRectangle(new Rect(40 * unit, height - 140 * unit, 760 * unit, 90 * unit), 16 * unit, 16 * unit, Color.FromArgb((byte)(200 * fade), 20, 20, 30));
        ds.DrawText("Windshot video spike", new Vector2(64 * unit, height - 125 * unit), Color.FromArgb((byte)(255 * fade), 255, 255, 255),
            new CanvasTextFormat { FontSize = 44 * unit });
    }

    private static class Colors
    {
        public static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    }

    /// <summary>Adds one frame shown for <paramref name="shown"/>; GIF delays are in hundredths of a second.</summary>
    private static async Task<int> AddGifFrameAsync(BitmapEncoder gif, byte[] pixels, int width, int height, TimeSpan shown, bool notFirst)
    {
        if (notFirst)
            await gif.GoToNextFrameAsync();
        // Decoded video has no alpha (often 0s), and WIC's GIF encoder honors alpha even when
        // told to ignore it, so frames would turn see-through. Make every pixel opaque.
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        gif.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        int delay = Math.Max(2, (int)Math.Round(shown.TotalMilliseconds / 10));
        await gif.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
        {
            ["/grctlext/Delay"] = new BitmapTypedValue((ushort)delay, Windows.Foundation.PropertyType.UInt16),
        });
        return delay;
    }
}
