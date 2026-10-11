using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Windshot.Video;

/// <summary>
/// Writes H.264 (+ AAC) MP4 with Windows' own encoders: a MediaStreamSource that hands out the
/// frames and audio it's given, transcoded to a file. Frames are Direct3D surfaces (Win2D
/// render targets), so they reach the hardware encoder without a trip through the CPU.
/// </summary>
internal sealed class Mp4Writer
{
    private readonly BlockingCollection<(IDirect3DSurface Surface, TimeSpan Time, Action Done)> _video = new(boundedCapacity: 6);
    private readonly BlockingCollection<AudioChunk>? _audio;
    // Samples the encoder still has. Held here so they (and their Processed handlers) can't be
    // garbage-collected first, which would lose that frame's render target for good.
    private readonly ConcurrentDictionary<MediaStreamSample, byte> _inFlight = new();
    private IAsyncActionWithProgress<double>? _operation;
    private Task? _transcode;

    /// <param name="audio">16-bit PCM to mux in, or null for no sound.</param>
    public Mp4Writer(int width, int height, int fps, BlockingCollection<AudioChunk>? audio, int sampleRate, int channels)
    {
        _audio = audio;
        var video = new VideoStreamDescriptor(VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height));
        Source = audio is null
            ? new MediaStreamSource(video)
            : new MediaStreamSource(video, new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm((uint)sampleRate, (uint)channels, 16)));
        Source.BufferTime = TimeSpan.Zero;
        Source.SampleRequested += OnSampleRequested;

        Profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        Profile.Video!.Width = (uint)width;
        Profile.Video.Height = (uint)height;
        Profile.Video.FrameRate.Numerator = (uint)fps;
        Profile.Video.FrameRate.Denominator = 1;
        // About 0.1 bits per pixel per frame: crisp text without huge files.
        Profile.Video.Bitrate = (uint)Math.Clamp(width * height * fps * 0.1, 2_000_000, 40_000_000);
        // A keyframe every two seconds (CODECAPI_AVEncMPVGOPSize), so seeking, the timeline's
        // thumbnails and trimmed exports never decode far to reach a frame.
        Profile.Video.Properties[new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1")] = (uint)(fps * 2);
        Profile.Audio = audio is null ? null : AudioEncodingProperties.CreateAac((uint)sampleRate, (uint)channels, 192_000);
    }

    private MediaStreamSource Source { get; }
    private MediaEncodingProfile Profile { get; }

    public async Task StartAsync(string path)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(Source, stream, Profile);
        if (!prepared.CanTranscode)
        {
            stream.Dispose();
            throw new InvalidOperationException($"Can't encode video: {prepared.FailureReason}");
        }
        _operation = prepared.TranscodeAsync();
        _transcode = Task.Run(async () =>
        {
            try
            {
                await _operation;
            }
            finally
            {
                stream.Dispose();
            }
        });
    }

    /// <summary>
    /// Queues a frame; <paramref name="done"/> runs once the encoder is finished with the surface.
    /// Waits while the encoder is behind (export), or with <paramref name="wait"/> false returns
    /// false instead (recording, which drops the frame).
    /// </summary>
    public bool AddFrame(IDirect3DSurface surface, TimeSpan time, Action done, bool wait)
    {
        if (!wait)
            return _video.TryAdd((surface, time, done));
        _video.Add((surface, time, done));
        return true;
    }

    /// <summary>Ends the video stream and waits for the file to be finished.</summary>
    public async Task FinishAsync()
    {
        _video.CompleteAdding();
        if (_transcode is not null)
            await _transcode;
    }

    /// <summary>Stops without finishing the file (the caller deletes it).</summary>
    public async Task CancelAsync()
    {
        _video.CompleteAdding();
        _operation?.Cancel();
        try
        {
            if (_transcode is not null)
                await _transcode;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException || ex.HResult == unchecked((int)0x800704C7))
        {
            // Cancelled, as asked.
        }
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        var deferral = request.GetDeferral();
        bool isVideo = request.StreamDescriptor is VideoStreamDescriptor;
        // Answered off this thread, so waiting for video never holds up audio (or the reverse).
        Task.Run(() =>
        {
            try
            {
                if (isVideo)
                {
                    if (_video.TryTake(out var frame, Timeout.Infinite))
                    {
                        var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, frame.Time);
                        _inFlight[sample] = 0;
                        sample.Processed += (_, _) =>
                        {
                            _inFlight.TryRemove(sample, out byte _);
                            frame.Done();
                        };
                        request.Sample = sample;
                    }
                }
                else if (_audio!.TryTake(out var chunk, Timeout.Infinite))
                {
                    var sample = MediaStreamSample.CreateFromBuffer(chunk.Pcm.AsBuffer(), chunk.Time);
                    sample.Duration = chunk.Duration;
                    request.Sample = sample;
                }
                // No sample: that stream has ended.
            }
            catch (Exception ex)
            {
                Log.Write($"Video sample failed: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        });
    }
}

/// <summary>16-bit interleaved PCM, with its time from the start of the video.</summary>
internal readonly record struct AudioChunk(byte[] Pcm, TimeSpan Time, TimeSpan Duration);
