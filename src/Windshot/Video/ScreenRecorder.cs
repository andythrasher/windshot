using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Windshot.Video;

/// <summary>
/// Records an area of one monitor to MP4: Windows.Graphics.Capture frames, cropped with Win2D
/// on the GPU into a few render targets that go straight to the hardware encoder, plus the
/// system's sound (WASAPI loopback). Windows sends frames only when something changes; the
/// encoder holds each until the next, so the file still plays at an even rate.
/// </summary>
internal sealed partial class ScreenRecorder : IDisposable
{
    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private readonly ConcurrentBag<CanvasRenderTarget> _free = new();
    private readonly List<CanvasRenderTarget> _targets = new();
    private readonly Rect _crop;
    private readonly TimeSpan _interval;
    private readonly Mp4Writer _writer;
    private readonly LoopbackAudio? _audio;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private long _startTicks;
    private long _lastSlot = -1;
    /// <summary>The last frame written, to hold at the end (see <see cref="HoldLastFrame"/>).</summary>
    private CanvasRenderTarget? _lastFrame;
    private int _kept, _dropped;

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Video pixels per screen pixel: 1, unless the area was too big to encode as it is.</summary>
    public double Scale { get; }
    private readonly System.Drawing.Size _source;
    public bool HasAudio => _audio is not null || _microphone is not null;

    /// <param name="area">In physical desktop pixels, on one monitor.</param>
    /// <param name="sound">Record the PC's sound (otherwise silence, until switched on).</param>
    /// <param name="microphone">Record the microphone too.</param>
    public ScreenRecorder(System.Drawing.Rectangle area, int fps, bool sound, bool microphone, string path)
    {
        Path = path;
        // H.264 encoders stop at about 4K (4096 wide, or 4096x2304 in all): bigger areas, like a
        // whole 5K or super-ultrawide monitor, are scaled down to fit rather than failing.
        Scale = Math.Min(1, Math.Min(Math.Min(4096.0 / area.Width, 4096.0 / area.Height), Math.Sqrt(4096.0 * 2304 / ((double)area.Width * area.Height))));
        // H.264 needs even sizes.
        Width = (int)(area.Width * Scale) & ~1;
        Height = (int)(area.Height * Scale) & ~1;
        _source = new System.Drawing.Size((int)Math.Round(Width / Scale), (int)Math.Round(Height / Scale));
        _interval = TimeSpan.FromSeconds(1.0 / fps);

        var monitor = MonitorFromPoint(new POINT { X = area.X + area.Width / 2, Y = area.Y + area.Height / 2 }, 2);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);
        _crop = new Rect(area.X - info.Monitor.Left, area.Y - info.Monitor.Top, _source.Width, _source.Height);
        Monitor = monitor;

        // The PC's sound is always taken (muted while its switch is off, so it can be switched
        // on mid-recording); the microphone only when asked for, so it isn't in use otherwise.
        try
        {
            _audio = new LoopbackAudio { Muted = !sound };
        }
        catch (Exception ex)
        {
            // No speakers, or no audio service.
            Log.Write($"No system sound to record: {ex.Message}");
        }
        if (microphone)
            _microphone = OpenMicrophone();
        BlockingCollection<AudioChunk>? chunks = null;
        int rate = 0, channels = 0;
        if (_audio is not null)
        {
            // Mixed, so the microphone can join later.
            _mixer = new AudioMixer(_audio);
            if (_microphone is not null)
                _mixer.Add(_microphone);
            (chunks, rate, channels) = (_mixer.Chunks, _mixer.SampleRate, _mixer.Channels);
        }
        else if (_microphone is not null)
        {
            (chunks, rate, channels) = (_microphone.Chunks, _microphone.SampleRate, _microphone.Channels);
        }
        _writer = new Mp4Writer(Width, Height, fps, chunks, rate, channels);
    }

    private IntPtr Monitor { get; }

    private readonly AudioMixer? _mixer;
    private LoopbackAudio? _microphone;

    /// <summary>Why the microphone couldn't be used, if it couldn't (e.g. turned off in Windows' privacy settings).</summary>
    public string? MicrophoneProblem { get; private set; }

    private LoopbackAudio? OpenMicrophone()
    {
        try
        {
            return new LoopbackAudio(microphone: true);
        }
        catch (Exception ex)
        {
            Log.Write($"No microphone to record: {ex.Message}");
            MicrophoneProblem = ex.HResult == unchecked((int)0x80070005)
                ? "Windows isn't letting Windshot use the microphone. Allow it in Settings > Privacy & security > Microphone."
                : "No microphone found";
            return null;
        }
    }

    /// <summary>Records silence instead of the PC's sound while set.</summary>
    public bool Muted
    {
        get => _audio?.Muted ?? true;
        set
        {
            if (_audio is not null)
                _audio.Muted = value;
        }
    }

    public bool HasMicrophone => _microphone is not null;

    /// <summary>Whether the PC's sound can be recorded (there are speakers to listen to).</summary>
    public bool HasSystemSound => _audio is not null;

    /// <summary>Whether the microphone can be switched on: it's on, or it can join the mix.</summary>
    public bool CanUseMicrophone => _microphone is not null || _mixer is not null;

    /// <summary>
    /// Records the microphone (or silence instead, while muted). Turned on mid-recording, it
    /// joins from then on; false if there's no microphone to use (see <see cref="MicrophoneProblem"/>).
    /// </summary>
    public bool SetMicrophone(bool on)
    {
        if (_microphone is null)
        {
            if (!on)
                return true;
            // Only into a mix with the PC's sound, which fixed the track's format at the start.
            if (_mixer is null || _startTicks == 0 || (_microphone = OpenMicrophone()) is null)
                return false;
            _microphone.Start(_startTicks);
            if (_pausedAt != 0)
                _microphone.Pause();
            _mixer.Add(_microphone);
        }
        _microphone.Muted = !on;
        return true;
    }

    /// <summary>When the recording was paused (QPC ticks), or 0 while it's running.</summary>
    private long _pausedAt;

    public bool Paused => _pausedAt != 0;

    /// <summary>Stops recording until <see cref="Resume"/>; the paused time is left out of the video.</summary>
    public void Pause()
    {
        if (_pausedAt != 0 || _startTicks == 0)
            return;
        _pausedAt = LoopbackAudio.NowTicks;
        _audio?.Pause();
        _microphone?.Pause();
    }

    public void Resume()
    {
        if (_pausedAt == 0)
            return;
        long gap = LoopbackAudio.NowTicks - _pausedAt;
        _audio?.Resume(gap);
        _microphone?.Resume(gap);
        Interlocked.Add(ref _startTicks, gap);
        _pausedAt = 0;
    }

    /// <summary>How much has been recorded: the time since starting, less any pauses.</summary>
    public TimeSpan Elapsed => _startTicks == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((_pausedAt != 0 ? _pausedAt : LoopbackAudio.NowTicks) - _startTicks);

    public async Task StartAsync(bool cursor)
    {
        var item = CreateItemForMonitor(Monitor);
        for (int i = 0; i < 8; i++)
        {
            var target = new CanvasRenderTarget(_device, Width, Height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Ignore);
            _targets.Add(target);
            _free.Add(target);
        }
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = cursor;
        try
        {
            // Without this, Windows draws a yellow border around the monitor while recording.
            // Checked, never asked here: the question comes before the area is picked (see AskForBorderlessAsync).
            var access = BorderlessAccess.CheckAccess();
            if (access == AppCapabilityAccessStatus.Allowed)
                _session.IsBorderRequired = false;
            else
                Log.Write($"Recording keeps Windows' border: borderless capture is {access}");
        }
        catch (Exception ex)
        {
            Log.Write($"Recording keeps Windows' border: {ex.Message}");
        }
        try
        {
            _session.MinUpdateInterval = _interval - TimeSpan.FromMilliseconds(2);
        }
        catch (Exception ex)
        {
            Log.Write($"Recording without a frame-rate cap from Windows: {ex.Message}");
        }

        await _writer.StartAsync(Path);
        _startTicks = LoopbackAudio.NowTicks;
        _audio?.Start(_startTicks);
        _microphone?.Start(_startTicks);
        _mixer?.Start();
        _session.StartCapture();
    }

    private static AppCapability BorderlessAccess => AppCapability.Create("graphicsCaptureWithoutBorder");

    /// <summary>
    /// The Store app needs the user's OK to record without Windows' yellow border, and Windows
    /// asks once. Asked before the area is picked, so recording never waits on the question.
    /// </summary>
    public static async Task AskForBorderlessAsync()
    {
        try
        {
            if (BorderlessAccess.CheckAccess() == AppCapabilityAccessStatus.UserPromptRequired)
                Log.Write($"Borderless recording: {await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)}");
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't ask about borderless recording: {ex.Message}");
        }
    }

    /// <summary>Finishes the file; returns how long the recording is.</summary>
    public async Task<TimeSpan> StopAsync()
    {
        var length = Elapsed;
        EndCapture();
        HoldLastFrame(length);
        _audio?.Stop(_startTicks + length.Ticks);
        _microphone?.Stop(_startTicks + length.Ticks);
        await _writer.FinishAsync();
        Log.Write($"Recorded {length.TotalSeconds:F1} s at {Width}x{Height}{(Scale < 1 ? $" (scaled from {_source.Width}x{_source.Height})" : "")}: {_kept} frames kept, {_dropped} dropped{(_audio is null ? ", no sound" : "")}{(_microphone is not null ? ", with the microphone" : "")}");
        return length;
    }

    /// <summary>
    /// Windows only sends a frame when the screen changes, so if nothing moved at the end, the
    /// file would stop at the last change. The last frame goes in again at the end, so the video
    /// lasts as long as the recording (and its sound).
    /// </summary>
    private void HoldLastFrame(TimeSpan length)
    {
        long finalSlot = (long)Math.Round(length / _interval) - 1;
        if (_lastFrame is not { } last || finalSlot <= _lastSlot)
            return;
        try
        {
            // Twice, a frame apart: the encoder makes the last frame as long as the gap before it.
            for (long slot = Math.Max(_lastSlot + 1, finalSlot - 1); slot <= finalSlot; slot++)
            {
                var copy = new CanvasRenderTarget(_device, Width, Height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Ignore);
                using (var ds = copy.CreateDrawingSession())
                    ds.DrawImage(last);
                _writer.AddFrame(copy, _interval * slot, copy.Dispose, wait: true);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Holding the last frame failed: {ex.Message}");
        }
    }

    public async Task CancelAsync()
    {
        EndCapture();
        _audio?.Stop(LoopbackAudio.NowTicks);
        _microphone?.Stop(LoopbackAudio.NowTicks);
        await _writer.CancelAsync();
    }

    private void EndCapture()
    {
        _session?.Dispose();
        _pool?.Dispose();
        _session = null;
        _pool = null;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
                return;
            var time = TimeSpan.FromTicks(frame.SystemRelativeTime.Ticks - _startTicks);
            // Paused: nothing goes in (and frames from before a resume are behind the new start).
            if (_pausedAt != 0 || time < TimeSpan.Zero)
                return;
            // Frames keep to a grid of 1/fps slots, so arrival jitter can't halve the rate.
            long slot = (long)Math.Round(time / _interval);
            if (slot <= _lastSlot)
                return;
            if (!_free.TryTake(out var target))
            {
                _dropped++;
                return;
            }
            using (var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface))
            using (var ds = target.CreateDrawingSession())
                ds.DrawImage(bitmap, new Rect(0, 0, Width, Height), _crop, 1, Scale < 1 ? CanvasImageInterpolation.HighQualityCubic : CanvasImageInterpolation.NearestNeighbor);
            if (_writer.AddFrame(target, time, () => _free.Add(target), wait: false))
            {
                _lastSlot = slot;
                _lastFrame = target;
                _kept++;
            }
            else
            {
                _free.Add(target);
                _dropped++;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Recording frame failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        EndCapture();
        _audio?.Dispose();
        _microphone?.Dispose();
        foreach (var target in _targets)
            target.Dispose();
    }

    // ---- A capture item for a monitor, through IGraphicsCaptureItemInterop ----

    private static unsafe GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interopIid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        Com.Check(Marshal.QueryInterface(factory.ThisPtr, in interopIid, out var interop), "QueryInterface(IGraphicsCaptureItemInterop)");
        try
        {
            var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            IntPtr result;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Com.Slot(interop, 4))(interop, monitor, &itemIid, &result), "CreateForMonitor");
            var item = GraphicsCaptureItem.FromAbi(result);
            Marshal.Release(result);
            return item;
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
}
