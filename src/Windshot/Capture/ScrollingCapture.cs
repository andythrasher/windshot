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

    private ScrollingCapture(Rectangle area, double scale, Action<CapturedImage> onCaptured)
    {
        _area = area;
        _scale = scale;
        _onCaptured = onCaptured;
        _frame = new AreaFrame(area, scale);
        _panel = new ScrollPanel(area, scale);
        _panel.Done += Finish;
        _panel.Cancelled += Cancel;
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
            while (!token.IsCancellationRequested)
            {
                long started = clock.ElapsedMilliseconds;
                var result = stitcher.Add(grabber.Grab());
                int height = stitcher.TotalHeight;
                if (result != StitchResult.Unchanged)
                    ui.Post(_ => _panel.ShowProgress(result, height), null);
                if (result == StitchResult.Full)
                {
                    ui.Post(_ => Finish(), null);
                    return;
                }
                int wait = FrameIntervalMs - (int)(clock.ElapsedMilliseconds - started);
                if (wait > 0)
                    token.WaitHandle.WaitOne(wait);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Scrolling capture failed: {ex}");
            ui.Post(_ => Cancel(), null);
        }
    }

    private void Finish()
    {
        if (!Stop())
            return;
        if (_stitcher is null)
            return;
        var image = _stitcher.Build(_scale, _area);
        Log.Write($"Scrolling capture finished: {image.Width} x {image.Height}");
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

    // ---- Windows -----------------------------------------------------------------------

    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x8;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>Keeps a window out of screen captures, so it never ends up in a frame even if it overlaps the area.</summary>
    private static void ExcludeFromCapture(Form form)
    {
        if (!SetWindowDisplayAffinity(form.Handle, WDA_EXCLUDEFROMCAPTURE))
            Log.Write($"Couldn't exclude {form.GetType().Name} from capture (error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>A click-through outline just outside the area being captured.</summary>
    private sealed class AreaFrame : Form
    {
        private static readonly Color Key = Color.FromArgb(255, 0, 255);
        private static readonly Color Outline = Color.FromArgb(64, 132, 214);
        private readonly int _thickness;

        public AreaFrame(Rectangle area, double scale)
        {
            _thickness = Math.Max(2, (int)Math.Round(2 * scale));
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Key;
            TransparencyKey = Key;
            var bounds = area;
            bounds.Inflate(_thickness, _thickness);
            Bounds = bounds;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ExcludeFromCapture(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var pen = new Pen(Outline, _thickness) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
            e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }

    /// <summary>Progress and the Done and Cancel buttons, beside the area. Doesn't take focus, so keys still scroll the app.</summary>
    private sealed class ScrollPanel : Form
    {
        private readonly Label _status;
        private readonly Font _font;

        public ScrollPanel(Rectangle area, double scale)
        {
            int S(double dip) => (int)Math.Round(dip * scale);
            _font = new Font("Segoe UI", (float)(14 * scale), FontStyle.Regular, GraphicsUnit.Pixel);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = Color.FromArgb(32, 32, 32);
            Font = _font;

            _status = new Label
            {
                ForeColor = Color.White,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(S(14), 0, S(250), S(44)),
                Text = "Scroll down to capture more",
            };
            var done = MakeButton("Done", Color.FromArgb(64, 132, 214), new Rectangle(S(270), S(8), S(72), S(28)));
            var cancel = MakeButton("Cancel", Color.FromArgb(64, 64, 64), new Rectangle(S(348), S(8), S(72), S(28)));
            done.Click += (_, _) => Done?.Invoke();
            cancel.Click += (_, _) => Cancelled?.Invoke();
            Controls.AddRange([_status, done, cancel]);

            var size = new Size(S(428), S(44));
            Size = size;
            Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, S(12), S(12)));
            Location = Place(area, size, S(10));
        }

        public event Action? Done;
        public event Action? Cancelled;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ExcludeFromCapture(this);
        }

        public void ShowProgress(StitchResult result, int height)
        {
            if (IsDisposed)
                return; // a last update can arrive after Done
            string size = $"{height:N0} px";
            _status.Text = result switch
            {
                StitchResult.Lost => "Lost track: scroll back up a little",
                StitchResult.ScrolledBack => $"Scroll down to continue · {size}",
                StitchResult.Full => $"Reached the maximum height · {size}",
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

        private Button MakeButton(string text, Color color, Rectangle bounds)
        {
            var button = new Button
            {
                Text = text,
                Bounds = bounds,
                FlatStyle = FlatStyle.Flat,
                BackColor = color,
                ForeColor = Color.White,
                Font = _font,
                TabStop = false,
                Cursor = Cursors.Hand,
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        /// <summary>Below the area if there's room on its monitor, else above, else inside along the bottom.</summary>
        private static Point Place(Rectangle area, Size size, int gap)
        {
            var monitor = Screen.FromRectangle(area).WorkingArea;
            int x = Math.Clamp(area.X + (area.Width - size.Width) / 2, monitor.Left, Math.Max(monitor.Left, monitor.Right - size.Width));
            if (area.Bottom + gap + size.Height <= monitor.Bottom)
                return new Point(x, area.Bottom + gap);
            if (area.Top - gap - size.Height >= monitor.Top)
                return new Point(x, area.Top - gap - size.Height);
            return new Point(x, Math.Min(area.Bottom, monitor.Bottom) - gap - size.Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _font.Dispose();
            base.Dispose(disposing);
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
    }
}
