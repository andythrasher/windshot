using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace VideoSpike;

/// <summary>
/// Writes H.264 (+ AAC) MP4 with Windows' own encoders: a MediaStreamSource that hands out the
/// frames and audio it's given, transcoded to a file. Frames are Direct3D surfaces (Win2D
/// render targets), so they go to the hardware encoder without a trip through the CPU.
/// </summary>
internal sealed class Mp4Writer
{
    private readonly BlockingCollection<(IDirect3DSurface Surface, TimeSpan Time, Action Done)> _video = new(boundedCapacity: 6);
    private readonly BlockingCollection<(byte[] Pcm, TimeSpan Time, TimeSpan Duration)>? _audio;
    private readonly VideoStreamDescriptor _videoStream;
    private Task? _transcode;

    public int VideoSamples { get; private set; }
    public int AudioSamples { get; private set; }
    public int Processed;
    // Samples the encoder still has. Held here so they (and their Processed handlers) can't be
    // garbage-collected first, which would lose the frame's render target for good.
    private readonly ConcurrentDictionary<MediaStreamSample, byte> _inFlight = new();

    public Mp4Writer(int width, int height, int fps, BlockingCollection<(byte[] Pcm, TimeSpan Time, TimeSpan Duration)>? audio, int sampleRate, int channels)
    {
        _audio = audio;
        var videoProps = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        _videoStream = new VideoStreamDescriptor(videoProps);
        Source = audio is null
            ? new MediaStreamSource(_videoStream)
            : new MediaStreamSource(_videoStream, new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm((uint)sampleRate, (uint)channels, 16)));
        Source.BufferTime = TimeSpan.Zero;
        Source.SampleRequested += OnSampleRequested;

        Profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        Profile.Video!.Width = (uint)width;
        Profile.Video.Height = (uint)height;
        Profile.Video.FrameRate.Numerator = (uint)fps;
        Profile.Video.FrameRate.Denominator = 1;
        // About 0.1 bits per pixel per frame: crisp text without huge files.
        Profile.Video.Bitrate = (uint)Math.Clamp(width * height * fps * 0.1, 2_000_000, 40_000_000);
        Profile.Audio = audio is null ? null : AudioEncodingProperties.CreateAac((uint)sampleRate, (uint)channels, 192_000);
    }

    public MediaStreamSource Source { get; }
    public MediaEncodingProfile Profile { get; }

    public async Task StartAsync(string path)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(Source, stream, Profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException($"Can't encode: {prepared.FailureReason}");
        _transcode = Task.Run(async () =>
        {
            try
            {
                await prepared.TranscodeAsync();
            }
            finally
            {
                stream.Dispose();
            }
        });
    }

    /// <summary>Blocks while the encoder is behind (offline export); returns false if it's full and <paramref name="wait"/> is false (live recording drops the frame).</summary>
    public bool AddFrame(IDirect3DSurface surface, TimeSpan time, Action done, bool wait)
    {
        if (wait)
        {
            _video.Add((surface, time, done));
            return true;
        }
        return _video.TryAdd((surface, time, done));
    }

    public async Task FinishAsync()
    {
        _video.CompleteAdding();
        if (_transcode is not null)
            await _transcode;
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
                        sample.Processed += (_, _) => { _inFlight.TryRemove(sample, out byte _); Interlocked.Increment(ref Processed); frame.Done(); };
                        request.Sample = sample;
                        VideoSamples++;
                    }
                }
                else if (_audio!.TryTake(out var chunk, Timeout.Infinite))
                {
                    var sample = MediaStreamSample.CreateFromBuffer(chunk.Pcm.AsBuffer(), chunk.Time);
                    sample.Duration = chunk.Duration;
                    request.Sample = sample;
                    AudioSamples++;
                }
                // No sample: that stream has ended.
            }
            finally
            {
                deferral.Complete();
            }
        });
    }
}
