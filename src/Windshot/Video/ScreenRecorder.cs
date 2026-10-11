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
    private int _kept, _dropped;

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public bool HasAudio => _audio is not null;

    /// <param name="area">In physical desktop pixels, on one monitor.</param>
    public ScreenRecorder(System.Drawing.Rectangle area, int fps, bool sound, string path)
    {
        Path = path;
        // H.264 needs even sizes.
        Width = area.Width & ~1;
        Height = area.Height & ~1;
        _interval = TimeSpan.FromSeconds(1.0 / fps);

        var monitor = MonitorFromPoint(new POINT { X = area.X + area.Width / 2, Y = area.Y + area.Height / 2 }, 2);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);
        _crop = new Rect(area.X - info.Monitor.Left, area.Y - info.Monitor.Top, Width, Height);
        Monitor = monitor;

        if (sound)
        {
            try
            {
                _audio = new LoopbackAudio();
            }
            catch (Exception ex)
            {
                // No speakers, or no audio service: record without sound.
                Log.Write($"No system sound to record: {ex.Message}");
            }
        }
        _writer = new Mp4Writer(Width, Height, fps, _audio?.Chunks, _audio?.SampleRate ?? 0, _audio?.Channels ?? 0);
    }

    private IntPtr Monitor { get; }

    /// <summary>Records silence instead of the sound while set.</summary>
    public bool Muted
    {
        get => _audio?.Muted ?? true;
        set
        {
            if (_audio is not null)
                _audio.Muted = value;
        }
    }

    public TimeSpan Elapsed => _startTicks == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(LoopbackAudio.NowTicks - _startTicks);

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
        _audio?.Stop(_startTicks + length.Ticks);
        await _writer.FinishAsync();
        Log.Write($"Recorded {length.TotalSeconds:F1} s at {Width}x{Height}: {_kept} frames kept, {_dropped} dropped{(_audio is null ? ", no sound" : "")}");
        return length;
    }

    public async Task CancelAsync()
    {
        EndCapture();
        _audio?.Stop(LoopbackAudio.NowTicks);
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
            if (time < TimeSpan.Zero)
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
                ds.DrawImage(bitmap, new Rect(0, 0, Width, Height), _crop, 1, CanvasImageInterpolation.NearestNeighbor);
            if (_writer.AddFrame(target, time, () => _free.Add(target), wait: false))
            {
                _lastSlot = slot;
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
