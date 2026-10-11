using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Windshot.Capture;

/// <summary>
/// Scrolling capture: pick an area, then scroll it yourself while frames are grabbed and
/// stitched into one tall image (see <see cref="ScrollStitcher"/>). A frame marks the area
/// and a small panel shows progress, with Done and Cancel; the hotkey again also finishes.
/// </summary>
internal sealed class ScrollingCapture
{
    private const int FrameIntervalMs = 80;

    private static ScrollingCapture? _current;

    private readonly Rectangle _area;
    private readonly double _scale;
    private readonly Action<CapturedImage> _onCaptured;
    private readonly AreaFrame _frame;
    private readonly ScrollPanel _panel;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _worker;
    private ScrollStitcher? _stitcher;
    private bool _ended;
    /// <summary>Set from the panel, read by the worker thread.</summary>
    private volatile bool _autoScroll;
    private readonly Rectangle _panelBounds;

    private ScrollingCapture(Rectangle area, double scale, Action<CapturedImage> onCaptured)
    {
        _area = area;
        _scale = scale;
        _onCaptured = onCaptured;
        _frame = new AreaFrame(area, scale, Color.FromArgb(64, 132, 214));
        _panel = new ScrollPanel(area, scale);
        _panelBounds = _panel.Bounds;
        _panel.Done += Finish;
        _panel.Cancelled += Cancel;
        _panel.AutoScrollToggled += on =>
        {
            _autoScroll = on;
            Log.Write($"Auto-scroll {(on ? "on" : "off")}");
        };
    }

    /// <summary>Selects an area and starts; while a capture is running, finishes it instead.</summary>
    public static void Begin(Action<CapturedImage> onCaptured)
    {
        if (_current is not null)
        {
            _current.Finish();
            return;
        }

        CaptureSession.SelectArea((area, scale) =>
        {
            // Overlays live per monitor, but a clicked window can hang off the edge.
            area = Rectangle.Intersect(area, Screen.FromRectangle(area).Bounds);
            if (area.Width < 16 || area.Height < 16)
                return;
            _current = new ScrollingCapture(area, scale, onCaptured);
            _current.Start();
        }, hint: "Select the area to scroll, or click a window");
    }

    private void Start()
    {
        Log.Write($"Scrolling capture of {_area} at scale {_scale}");
        _frame.Show();
        _panel.Show();

        // The largest bitmap the editor can hold, less room for a beautify backdrop.
        int maxHeight = Math.Max(_area.Height, (int)Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice().MaximumBitmapSizeInPixels - 1024);
        var ui = SynchronizationContext.Current!;
        var token = _stop.Token;
        _worker = new Thread(() => Run(maxHeight, ui, token)) { IsBackground = true, Name = "Scrolling capture" };
        _worker.Start();
    }

    /// <summary>Grabs and stitches frames until stopped. Runs on its own thread so the panel stays responsive.</summary>
    private void Run(int maxHeight, SynchronizationContext ui, CancellationToken token)
    {
        try
        {
            using var grabber = new AreaGrabber(_area);
            // Let the selection overlay finish disappearing before the first frame.
            Thread.Sleep(150);
            var stitcher = new ScrollStitcher(grabber.Grab(), _area.Width, _area.Height, maxHeight);
            _stitcher = stitcher;
            ui.Post(_ => _panel.ShowProgress(StitchResult.Unchanged, stitcher.TotalHeight), null);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            AutoScroller? auto = null;
            int[]? previous = null;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    long started = clock.ElapsedMilliseconds;
                    var frame = grabber.Grab();
                    bool still = previous is not null && IsStill(previous, frame);
                    previous = frame;
                    var result = stitcher.Add(frame);
                    int height = stitcher.TotalHeight;
                    if (result != StitchResult.Unchanged)
                        ui.Post(_ => _panel.ShowProgress(result, height), null);
                    if (result == StitchResult.Full)
                    {
                        ui.Post(_ => Finish(), null);
                        return;
                    }

                    // Auto-scroll is switched on and off from the panel; it's driven from here so it can react to each frame.
                    if (_autoScroll && auto is null)
                        auto = new AutoScroller(_area, _panelBounds);
                    else if (!_autoScroll && auto is not null)
                    {
                        auto.Dispose();
                        auto = null;
                    }
                    if (auto is not null && !token.IsCancellationRequested)
                    {
                        var state = auto.Tick(result, still, clock.ElapsedMilliseconds);
                        if (state == AutoScrollState.AtEnd)
                        {
                            Log.Write("Auto-scroll reached the end");
                            ui.Post(_ => Finish(), null);
                            return;
                        }
                        if (state != AutoScrollState.Running)
                        {
                            Log.Write($"Auto-scroll stopped: {state}");
                            _autoScroll = false;
                            string message = state switch
                            {
                                AutoScrollState.Stuck => "This window didn't scroll; scroll it yourself",
                                AutoScrollState.NoMatch => "Couldn't line this up; scroll it yourself",
                                _ => "Auto-scroll paused",
                            };
                            ui.Post(_ => _panel.ShowAutoStopped(message), null);
                        }
                    }

                    int wait = FrameIntervalMs - (int)(clock.ElapsedMilliseconds - started);
                    if (wait > 0)
                        token.WaitHandle.WaitOne(wait);
                }
            }
            finally
            {
                auto?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Scrolling capture failed: {ex}");
            ui.Post(_ => Cancel(), null);
        }
    }

    /// <summary>
    /// Whether the content has stopped moving: at most a few rows changed. A spinner or a
    /// blinking caret somewhere in the area changes a few rows forever; scrolling changes nearly all.
    /// </summary>
    private bool IsStill(int[] previous, int[] frame)
    {
        int width = _area.Width, changed = 0, allowed = Math.Max(2, _area.Height / 33);
        for (int y = 0; y < _area.Height; y++)
        {
            if (!frame.AsSpan(y * width, width).SequenceEqual(previous.AsSpan(y * width, width)) && ++changed > allowed)
                return false;
        }
        return true;
    }

    private void Finish()
    {
        if (!Stop())
            return;
        if (_stitcher is null)
            return;
        var image = _stitcher.Build(_scale, _area);
        Log.Write($"Scrolling capture finished: {image.Width} x {image.Height}, footer {_stitcher.Footer}");
        _onCaptured(image);
    }

    private void Cancel()
    {
        if (Stop())
            Log.Write("Scrolling capture cancelled");
    }

    /// <returns>False if it had already ended.</returns>
    private bool Stop()
    {
        if (_ended)
            return false;
        _ended = true;
        _stop.Cancel();
        // The worker finishes its current frame (well under a second) and then stops touching the stitcher.
        if (_worker is not null && _worker != Thread.CurrentThread)
            _worker.Join(2000);
        _frame.Close();
        _panel.Close();
        _current = null;
        return true;
    }

    /// <summary>Copies one screen area into an int-per-pixel buffer, reusing the same bitmap each time.</summary>
    private sealed class AreaGrabber(Rectangle area) : IDisposable
    {
        private readonly Bitmap _bitmap = new(area.Width, area.Height, PixelFormat.Format32bppRgb);

        public int[] Grab()
        {
            using (var g = Graphics.FromImage(_bitmap))
                g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);

            var pixels = new int[area.Width * area.Height];
            var data = _bitmap.LockBits(new Rectangle(Point.Empty, area.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < area.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * area.Width, area.Width);
            }
            finally
            {
                _bitmap.UnlockBits(data);
            }

            // The alpha byte of an RGB bitmap is undefined; make it opaque.
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] |= unchecked((int)0xFF000000);
            return pixels;
        }

        public void Dispose() => _bitmap.Dispose();
    }

    /// <summary>Progress and the Done and Cancel buttons, beside the area. Doesn't take focus, so keys still scroll the app.</summary>
    private sealed class ScrollPanel : FloatingPanel
    {
        private const string AutoLabel = "Auto-scroll";
        private readonly Label _status;
        private readonly Button _autoButton;
        private bool _auto;

        public ScrollPanel(Rectangle area, double scale) : base(scale)
        {
            _status = new Label
            {
                ForeColor = Color.White,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(S(14), 0, S(250), S(44)),
                Text = "Scroll down to capture more",
            };
            _autoButton = MakeButton(AutoLabel, Plain, new Rectangle(S(270), S(8), S(104), S(28)));
            var done = MakeButton("Done", Accent, new Rectangle(S(380), S(8), S(72), S(28)));
            var cancel = MakeButton("Cancel", Plain, new Rectangle(S(458), S(8), S(72), S(28)));
            _autoButton.Click += (_, _) => SetAuto(!_auto);
            done.Click += (_, _) => Done?.Invoke();
            cancel.Click += (_, _) => Cancelled?.Invoke();
            Controls.AddRange([_status, _autoButton, done, cancel]);
            Layout(area, new Size(S(538), S(44)));
        }

        public event Action? Done;
        public event Action? Cancelled;
        public event Action<bool>? AutoScrollToggled;

        private void SetAuto(bool on)
        {
            _auto = on;
            _autoButton.Text = on ? "Stop" : AutoLabel;
            if (on)
                _status.Text = "Auto-scrolling…";
            AutoScrollToggled?.Invoke(on);
        }

        /// <summary>Auto-scroll gave up or was interrupted; back to scrolling by hand.</summary>
        public void ShowAutoStopped(string message)
        {
            if (IsDisposed)
                return;
            _auto = false;
            _autoButton.Text = AutoLabel;
            _status.Text = message;
        }

        public void ShowProgress(StitchResult result, int height)
        {
            if (IsDisposed)
                return; // a last update can arrive after Done
            string size = $"{height:N0} px";
            _status.Text = result switch
            {
                StitchResult.Full => $"Reached the maximum height · {size}",
                _ when _auto => $"Auto-scrolling · {size}", // auto-scroll recovers from the rest itself
                StitchResult.Lost => "Lost track: scroll back up a little",
                StitchResult.ScrolledBack => $"Scroll down to continue · {size}",
                _ => $"Scroll down · {size}",
            };
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
                Cancelled?.Invoke();
            else if (e.KeyCode == Keys.Enter)
                Done?.Invoke();
        }
    }
}
