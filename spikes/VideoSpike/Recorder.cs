using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace VideoSpike;

/// <summary>
/// Records a region of one monitor with Windows.Graphics.Capture: GPU frames, cropped with
/// Win2D into a small pool of render targets that go straight to the encoder.
/// </summary>
internal sealed partial class Recorder
{
    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private readonly ConcurrentBag<CanvasRenderTarget> _free = new();
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Mp4Writer? _writer;
    private Rect _crop;
    private long _startTicks;
    private TimeSpan _minInterval;
    private long _lastSlot = -1; // frames keep to a grid of 1/fps slots, so arrival jitter doesn't halve the rate

    public int Arrived, Kept, DroppedByCap, DroppedBusy;
    public string Notes = "";

    /// <param name="region">In physical desktop pixels; width and height even (H.264 needs that).</param>
    public async Task<(long Start, long End)> RecordAsync(System.Drawing.Rectangle region, int fps, TimeSpan length, Mp4Writer writer, string path, LoopbackAudio? audio)
    {
        _writer = writer;
        var monitor = MonitorFromPoint(new POINT { X = region.X + 1, Y = region.Y + 1 }, 2);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);
        _crop = new Rect(region.X - info.Monitor.Left, region.Y - info.Monitor.Top, region.Width, region.Height);
        _minInterval = TimeSpan.FromSeconds(1.0 / fps) - TimeSpan.FromMilliseconds(2);

        var item = CreateItemForMonitor(monitor);
        for (int i = 0; i < 8; i++)
            _free.Add(new CanvasRenderTarget(_device, region.Width, region.Height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Ignore));

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = true;
        try
        {
            var access = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            _session.IsBorderRequired = false;
            Notes += $"borderless: {access}; ";
        }
        catch (Exception ex)
        {
            Notes += $"borderless failed: {ex.Message}; ";
        }
        try
        {
            _session.MinUpdateInterval = _minInterval;
            Notes += "MinUpdateInterval set; ";
        }
        catch (Exception ex)
        {
            Notes += $"MinUpdateInterval failed: {ex.Message}; ";
        }

        await writer.StartAsync(path);
        _startTicks = LoopbackAudio.NowTicks;
        audio?.Start(_startTicks);
        _session.StartCapture();
        await Task.Delay(length);
        long end = LoopbackAudio.NowTicks;
        _session.Dispose();
        _pool.Dispose();
        audio?.Stop(end);
        await writer.FinishAsync();
        return (_startTicks, end);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
            return;
        Arrived++;
        var time = TimeSpan.FromTicks(frame.SystemRelativeTime.Ticks - _startTicks);
        if (time < TimeSpan.Zero)
            return;
        long slot = (long)Math.Round(time.TotalSeconds / (_minInterval.TotalSeconds + 0.002));
        if (slot <= _lastSlot)
        {
            DroppedByCap++;
            return;
        }
        if (!_free.TryTake(out var target))
        {
            DroppedBusy++;
            return;
        }
        using (var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface))
        using (var ds = target.CreateDrawingSession())
            ds.DrawImage(bitmap, new Rect(0, 0, _crop.Width, _crop.Height), _crop, 1, CanvasImageInterpolation.NearestNeighbor);
        if (_writer!.AddFrame(target, time, () => _free.Add(target), wait: false))
        {
            _lastSlot = slot;
            Kept++;
        }
        else
        {
            _free.Add(target);
            DroppedBusy++;
        }
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
