using System.Drawing;
using System.Windows.Forms;
using Windshot.Video;

namespace Windshot.Capture;

/// <summary>
/// Recording a video: pick an area (or click a window), then everything there is recorded,
/// with the PC's sound, until Stop or the shortcut again; Cancel throws it away. An outline
/// marks the area and a small bar shows the time; neither is in the recording. The finished
/// recording opens in the editor, starting from its first frame.
/// </summary>
internal sealed class ScreenRecording
{
    private static ScreenRecording? _current;

    /// <summary>Whether a recording is under way (or starting).</summary>
    public static bool IsRecording => _current is not null;

    private readonly Rectangle _area;
    private readonly double _scale;
    private readonly Action<CapturedImage> _onRecorded;
    private readonly Action<bool> _recordingChanged;
    private readonly AreaFrame _frame;
    private readonly RecordPanel _panel;
    private readonly ScreenRecorder _recorder;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 250 };
    private bool _ended;
    /// <summary>Starting takes a moment; stopping waits for it, so it never stops a recorder that's half started.</summary>
    private Task _starting = Task.CompletedTask;

    /// <summary>Where recordings wait while they're being edited; anything left here at startup is from a crash.</summary>
    public static string Folder { get; } = Path.Combine(AppPackage.DataFolder, "Recordings");

    private ScreenRecording(Rectangle area, double scale, Action<CapturedImage> onRecorded, Action<bool> recordingChanged)
    {
        _area = area;
        _scale = scale;
        _onRecorded = onRecorded;
        _recordingChanged = recordingChanged;
        var video = Settings.Current.Video;
        Directory.CreateDirectory(Folder);
        _recorder = new ScreenRecorder(area, video.FrameRate, video.Sound, Path.Combine(Folder, $"Recording {DateTime.Now:yyyy-MM-dd HHmmss}.mp4"));
        _frame = new AreaFrame(area, scale, Color.FromArgb(232, 64, 64));
        _panel = new RecordPanel(area, scale, soundOn: video.Sound && _recorder.HasAudio, hasSound: _recorder.HasAudio);
        _panel.Stop += () => _ = FinishAsync();
        _panel.Cancelled += () => _ = CancelAsync();
        _panel.SoundToggled += on =>
        {
            _recorder.Muted = !on;
            Settings.Current.Video.Sound = on;
            Settings.Save();
            Log.Write($"Recording sound {(on ? "on" : "off")}");
        };
        _clock.Tick += (_, _) => _panel.ShowTime(_recorder.Elapsed);
    }

    /// <summary>Set while Windows asks about recording without its border, so a second press doesn't ask twice.</summary>
    private static bool _asking;

    /// <summary>Selects an area and starts recording; while recording, stops instead.</summary>
    public static async void Begin(Action<CapturedImage> onRecorded, Action<bool> recordingChanged)
    {
        if (_current is not null)
        {
            _ = _current.FinishAsync();
            return;
        }
        if (_asking)
            return;
        _asking = true;
        try
        {
            // The first time only (Store app): before the area is picked, so nothing waits on it.
            await ScreenRecorder.AskForBorderlessAsync();
        }
        finally
        {
            _asking = false;
        }

        // Past the await, an exception would end the app (async void): keep it here.
        try
        {
            SelectAndRecord(onRecorded, recordingChanged);
        }
        catch (Exception ex)
        {
            Log.Write($"Recording failed: {ex}");
        }
    }

    private static void SelectAndRecord(Action<CapturedImage> onRecorded, Action<bool> recordingChanged)
    {
        CaptureSession.SelectArea((area, scale) =>
        {
            // Recording follows one monitor; a clicked window can hang off its edge.
            area = Rectangle.Intersect(area, Screen.FromRectangle(area).Bounds);
            if (area.Width < 32 || area.Height < 32)
                return;
            try
            {
                _current = new ScreenRecording(area, scale, onRecorded, recordingChanged);
                _current._starting = _current.StartAsync();
            }
            catch (Exception ex)
            {
                Log.Write($"Couldn't start recording: {ex}");
                _current = null;
                Shell.Hud.Show("Couldn't start recording", area, scale);
            }
        }, hint: "Select an area to record, or click a window");
    }

    private async Task StartAsync()
    {
        Log.Write($"Recording {_area} at scale {_scale}, {Settings.Current.Video.FrameRate} fps");
        _frame.Show();
        _panel.Show();
        _recorder.Muted = !_panel.SoundOn;
        try
        {
            // Let the selection overlay finish disappearing first.
            await Task.Delay(150);
            await _recorder.StartAsync(Settings.Current.Video.Cursor);
            // Stopped or cancelled meanwhile: that finishes (or throws away) what was started.
            if (_ended)
                return;
            _clock.Start();
            _recordingChanged(true);
        }
        catch (Exception ex)
        {
            Log.Write($"Recording failed to start: {ex}");
            if (!End())
                return;
            DeleteQuietly(_recorder.Path);
            Shell.Hud.Show("Couldn't start recording", _area, _scale);
        }
    }

    private async Task FinishAsync()
    {
        if (!End())
            return;
        await Started();
        try
        {
            var length = await _recorder.StopAsync();
            _recorder.Dispose();
            if (length < TimeSpan.FromSeconds(0.3))
            {
                DeleteQuietly(_recorder.Path);
                return;
            }
            var first = await Task.Run(() => FirstFrame(_recorder.Path));
            var video = new RecordedVideo(_recorder.Path, length, _recorder.HasAudio, Settings.Current.Video.FrameRate);
            // Scaled down to encode, the video has fewer pixels per DIP than the screen did.
            _onRecorded(new CapturedImage(first.Pixels, first.Width, first.Height, _scale * _recorder.Scale, _area) { Video = video });
        }
        catch (Exception ex)
        {
            Log.Write($"Recording failed: {ex}");
            Shell.Hud.Show("Couldn't finish the recording", _area, _scale);
        }
    }

    private async Task CancelAsync()
    {
        if (!End())
            return;
        await Started();
        try
        {
            await _recorder.CancelAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"Cancelling the recording: {ex.Message}");
        }
        _recorder.Dispose();
        DeleteQuietly(_recorder.Path);
        Log.Write("Recording cancelled");
    }

    private async Task Started()
    {
        try
        {
            await _starting;
        }
        catch (Exception ex)
        {
            Log.Write($"Recording start: {ex.Message}");
        }
    }

    /// <returns>False if it had already ended.</returns>
    private bool End()
    {
        if (_ended)
            return false;
        _ended = true;
        _clock.Stop();
        _clock.Dispose();
        _frame.Close();
        _panel.Close();
        _current = null;
        _recordingChanged(false);
        return true;
    }

    /// <summary>The recording's first frame, which the editor shows until it plays or seeks.</summary>
    private static (byte[] Pixels, int Width, int Height) FirstFrame(string path)
    {
        using var reader = new MediaReader(path, audio: false);
        var frame = reader.Read() ?? throw new InvalidDataException("The recording has no frames");
        return (frame.Data, reader.Width, reader.Height);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>At startup: recordings still here belong to editors that are gone (a crash, or Quit).</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;
            foreach (var file in Directory.EnumerateFiles(Folder))
                DeleteQuietly(file);
            // Copied videos stay a day, so they can still be pasted after a restart.
            string copies = Path.Combine(Folder, "Copies");
            if (Directory.Exists(copies))
            {
                foreach (var file in Directory.EnumerateFiles(copies))
                {
                    if (DateTime.Now - File.GetLastWriteTime(file) > TimeSpan.FromDays(1))
                        DeleteQuietly(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't clean up old recordings: {ex.Message}");
        }
    }

    /// <summary>The time, the sound switch, Stop and Cancel, beside the area.</summary>
    private sealed class RecordPanel : FloatingPanel
    {
        private readonly Label _time;
        private readonly Button _sound;
        private bool _soundOn;

        public RecordPanel(Rectangle area, double scale, bool soundOn, bool hasSound) : base(scale)
        {
            _soundOn = soundOn;
            _time = new Label
            {
                ForeColor = Color.White,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(S(14), 0, S(96), S(44)),
                Text = "● 0:00",
            };
            _sound = MakeButton("", Plain, new Rectangle(S(112), S(8), S(112), S(28)));
            _sound.Enabled = hasSound;
            ShowSound();
            var stop = MakeButton("Stop", Color.FromArgb(200, 48, 48), new Rectangle(S(230), S(8), S(72), S(28)));
            var cancel = MakeButton("Cancel", Plain, new Rectangle(S(308), S(8), S(72), S(28)));
            _sound.Click += (_, _) =>
            {
                _soundOn = !_soundOn;
                ShowSound();
                SoundToggled?.Invoke(_soundOn);
            };
            stop.Click += (_, _) => Stop?.Invoke();
            cancel.Click += (_, _) => Cancelled?.Invoke();
            Controls.AddRange([_time, _sound, stop, cancel]);
            Layout(area, new Size(S(388), S(44)));
        }

        public event Action? Stop;
        public event Action? Cancelled;
        public event Action<bool>? SoundToggled;

        public bool SoundOn => _soundOn;

        private void ShowSound() => _sound.Text = _sound.Enabled ? (_soundOn ? "Sound on" : "Sound off") : "No sound";

        public void ShowTime(TimeSpan elapsed)
        {
            if (IsDisposed)
                return;
            // The dot blinks, like a recording light.
            string dot = elapsed.Milliseconds < 500 ? "●" : "○";
            _time.Text = $"{dot} {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
            _time.ForeColor = Color.FromArgb(255, 110, 110);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
                Cancelled?.Invoke();
            else if (e.KeyCode == Keys.Enter)
                Stop?.Invoke();
        }
    }
}

/// <summary>A finished recording, waiting in <see cref="ScreenRecording.Folder"/> while it's edited.</summary>
internal sealed record RecordedVideo(string Path, TimeSpan Length, bool HasAudio, int FrameRate);
