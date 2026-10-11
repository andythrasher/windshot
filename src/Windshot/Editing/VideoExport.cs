using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Windshot.Capture;
using Windshot.Video;

namespace Windshot.Editing;

/// <summary>
/// Turns an edited recording into a file: every frame in the trimmed range, decoded, with the
/// document (annotations, crop, beautify) drawn over it exactly as the editor shows it, written
/// as MP4 (with the sound, unless muted) or as a GIF.
/// </summary>
/// <remarks>
/// Decoding and encoding run on worker threads; drawing stays on the UI thread, where the
/// document lives, one frame per turn, so the editor stays responsive.
/// </remarks>
internal static class VideoExport
{
    /// <summary>GIFs are kept to this rate and width: past them, files get big fast.</summary>
    public const int GifFrameRate = 15;
    public const int GifMaxWidth = 960;

    public sealed record Options(bool Gif, int MaxHeight, bool Muted, TimeSpan From, TimeSpan To);

    public static async Task WriteAsync(Document document, RecordedVideo video, Options options, string path,
        IProgress<double> progress, CancellationToken cancel)
    {
        var device = CanvasDevice.GetSharedDevice();
        var bounds = document.Bounds;
        var (width, height) = OutputSize(bounds, options);
        float scale = (float)(width / bounds.Width);
        var transform = Matrix3x2.CreateTranslation(-(float)bounds.X, -(float)bounds.Y) * Matrix3x2.CreateScale(scale);

        using var reader = new MediaReader(video.Path, audio: false);
        var targets = new BlockingCollection<CanvasRenderTarget>();
        var all = new List<CanvasRenderTarget>();
        for (int i = 0; i < (options.Gif ? 1 : 8); i++)
        {
            var target = new CanvasRenderTarget(device, width, height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            all.Add(target);
            targets.Add(target);
        }

        Mp4Writer? mp4 = null;
        GifWriter? gif = null;
        Task? audioTask = null;
        try
        {
            if (options.Gif)
            {
                gif = new GifWriter(path, width, height);
            }
            else
            {
                var sound = video.HasAudio && !options.Muted ? new BlockingCollection<AudioChunk>(boundedCapacity: 200) : null;
                using var probe = sound is null ? null : new MediaReader(video.Path, audio: true);
                mp4 = new Mp4Writer(width, height, video.FrameRate, sound, probe?.SampleRate ?? 0, probe?.Channels ?? 0);
                await mp4.StartAsync(path);
                if (sound is not null)
                    audioTask = Task.Run(() => ReadAudio(video.Path, options.From, options.To, sound, cancel));
            }

            var length = options.To - options.From;
            var gifStep = TimeSpan.FromSeconds(1.0 / GifFrameRate);
            var nextGif = TimeSpan.Zero;
            (byte[] Pixels, TimeSpan Time)? pendingGif = null;
            double gifShown = 0; // hundredths of a second written so far, so rounding doesn't drift

            await Task.Run(() => reader.Seek(options.From), cancel);
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var next = await Task.Run(reader.Read, cancel);
                if (next is not { } frame || frame.Time >= options.To)
                    break;
                if (frame.Time < options.From)
                    continue; // decoded only to get from the keyframe to the exact start
                var time = frame.Time - options.From;
                if (options.Gif && time < nextGif)
                    continue;

                var target = await Task.Run(() => targets.Take(cancel), cancel);
                using (var bitmap = CanvasBitmap.CreateFromBytes(device, frame.Data, reader.Width, reader.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied))
                using (var ds = target.CreateDrawingSession())
                {
                    // Video has no transparency: what's around the content (without a backdrop) is black.
                    ds.Clear(Microsoft.UI.Colors.Black);
                    ds.Transform = transform;
                    var interpolation = document.ImageInterpolation;
                    document.ImageInterpolation = scale < 1 ? CanvasImageInterpolation.HighQualityCubic : CanvasImageInterpolation.NearestNeighbor;
                    document.Render(ds, frame: bitmap, time: frame.Time);
                    document.ImageInterpolation = interpolation;
                }

                if (gif is not null)
                {
                    var pixels = target.GetPixelBytes();
                    targets.Add(target);
                    if (pendingGif is { } previous)
                    {
                        int delay = GifDelay(ref gifShown, time);
                        await Task.Run(() => gif.AddFrame(previous.Pixels, delay), cancel);
                    }
                    pendingGif = (pixels, time);
                    while (nextGif <= time)
                        nextGif += gifStep;
                }
                else
                {
                    var t = target;
                    await Task.Run(() => mp4!.AddFrame(t, time, () => targets.Add(t), wait: true), cancel);
                }
                progress.Report(Math.Clamp(time / length, 0, 1));
            }

            if (gif is not null)
            {
                if (pendingGif is { } last)
                {
                    int delay = GifDelay(ref gifShown, length);
                    await Task.Run(() => gif.AddFrame(last.Pixels, Math.Max(2, delay)), cancel);
                }
                await Task.Run(gif.Finish, cancel);
            }
            else
            {
                if (audioTask is not null)
                    await audioTask;
                await mp4!.FinishAsync();
            }
            progress.Report(1);
        }
        catch
        {
            if (mp4 is not null)
                await mp4.CancelAsync();
            gif?.Dispose();
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            throw;
        }
        finally
        {
            gif?.Dispose();
            foreach (var target in all)
                target.Dispose();
        }
    }

    /// <summary>How long the previous GIF frame shows, in hundredths: rounded so the total stays on time.</summary>
    private static int GifDelay(ref double shown, TimeSpan until)
    {
        int delay = Math.Max(2, (int)Math.Round(until.TotalMilliseconds / 10 - shown));
        shown += delay;
        return delay;
    }

    /// <summary>The export's size: the canvas, scaled down to fit the size limit (and a GIF's width), in even numbers.</summary>
    public static (int Width, int Height) OutputSize(Windows.Foundation.Rect bounds, Options options)
    {
        double scale = 1;
        if (options.MaxHeight > 0 && bounds.Height > options.MaxHeight)
            scale = options.MaxHeight / bounds.Height;
        if (options.Gif && bounds.Width * scale > GifMaxWidth)
            scale = GifMaxWidth / bounds.Width;
        int width = Math.Max(2, (int)Math.Round(bounds.Width * scale / 2) * 2);
        int height = Math.Max(2, (int)Math.Round(bounds.Height * scale / 2) * 2);
        return (width, height);
    }

    /// <summary>The sound inside the trim, shifted to start at zero.</summary>
    private static void ReadAudio(string path, TimeSpan from, TimeSpan to, BlockingCollection<AudioChunk> sound, CancellationToken cancel)
    {
        try
        {
            using var audio = new MediaReader(path, audio: true);
            int frameBytes = audio.Channels * 2;
            long first = (long)(from.TotalSeconds * audio.SampleRate), last = (long)(to.TotalSeconds * audio.SampleRate);
            audio.Seek(from);
            while (!cancel.IsCancellationRequested && audio.Read() is { } chunk)
            {
                long at = (long)Math.Round(chunk.Time.TotalSeconds * audio.SampleRate);
                long frames = chunk.Data.Length / frameBytes;
                long start = Math.Max(at, first), end = Math.Min(at + frames, last);
                if (end <= start)
                {
                    if (at >= last)
                        break;
                    continue;
                }
                var pcm = chunk.Data.AsSpan((int)((start - at) * frameBytes), (int)((end - start) * frameBytes)).ToArray();
                sound.Add(new AudioChunk(pcm, TimeSpan.FromTicks((start - first) * 10_000_000 / audio.SampleRate),
                    TimeSpan.FromTicks((end - start) * 10_000_000 / audio.SampleRate)), cancel);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            sound.CompleteAdding();
        }
    }
}
