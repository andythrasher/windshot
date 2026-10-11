using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Windshot.Capture;
using Windshot.Video;

namespace Windshot.Editing;

/// <summary>
/// Editing a recording: the same editor, with a timeline along the bottom to play, scrub and
/// trim it (by dragging the strip's ends; what's trimmed away is dimmed, not deleted, like
/// crop). The bottom layer is the frame at the playhead, and annotations sit on every frame.
/// Save and Copy export the trimmed video, as MP4 or GIF.
/// </summary>
public sealed partial class EditorWindow
{
    private RecordedVideo? _video;
    private MediaPlayer? _player;
    private CanvasRenderTarget? _frameTarget;
    private bool _frameQueued;
    private DispatcherQueueTimer? _playbackTimer;
    private TimeSpan _length, _trimStart, _trimEnd, _position;
    private bool _playing;
    private bool _muted;
    private readonly List<CanvasBitmap> _thumbnails = new();
    private readonly CancellationTokenSource _closing = new();
    /// <summary>Changed since it was last saved or copied (a fresh recording counts as changed).</summary>
    private bool _unsaved = true;
    private bool _closeConfirmed;
    private CancellationTokenSource? _export;
    private bool _spacePanned;

    /// <summary>What a drag on the timeline is doing.</summary>
    private enum TimelineDrag { None, Scrub, TrimStart, TrimEnd }
    private TimelineDrag _timelineDrag;

    private const float StripInset = 10;
    private const float HandleWidth = 10;
    private static readonly TimeSpan MinTrimLength = TimeSpan.FromSeconds(0.5);

    private bool IsVideo => _video is not null;

    private TimeSpan FrameStep => TimeSpan.FromSeconds(1.0 / (_video?.FrameRate ?? 30));

    private void InitializeVideo(RecordedVideo video)
    {
        _video = video;
        _length = _trimEnd = video.Length;
        Title = "Windshot recording";
        Timeline.Visibility = Visibility.Visible;
        PinButton.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(CopyButton, "Copy the video (Ctrl+C), to paste into a chat, an email or a folder");
        if (Settings.Current.Saving.SaveWithoutAsking)
            ToolTipService.SetToolTip(SaveButton, $"Save to {Path.GetFileName(Settings.Current.Video.EffectiveFolder.TrimEnd(Path.DirectorySeparatorChar))} (Ctrl+S). Ctrl+Shift+S to save as.");
        foreach (var button in Toolbar.PrimaryCommands.OfType<AppBarToggleButton>())
        {
            if (button.Tag is "Blur")
                ToolTipService.SetToolTip(button, "Blur (B). In a video it stays where you put it: if what it hides moves, it won't follow.");
        }
        ImageLayerName.Text = "Recording";
        ImageLayerIcon.Content = Shell.FluentIcons.Create("video", filled: false);

        var size = _document.Image.SizeInPixels;
        _frameTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size.Width, size.Height, 96);

        _player = new MediaPlayer { IsVideoFrameServerEnabled = true, AutoPlay = false };
        // Keep it out of Windows' media controls (the volume flyout's "now playing").
        _player.CommandManager.IsEnabled = false;
        _player.VideoFrameAvailable += OnVideoFrame;
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() => SetPlaying(false));
        _player.MediaOpened += (sender, _) =>
        {
            var natural = sender.PlaybackSession.NaturalDuration;
            DispatcherQueue.TryEnqueue(() =>
            {
                // The file's own length is the one to trust, if it's known.
                if (natural > TimeSpan.Zero && _trimEnd == _length)
                    _length = _trimEnd = natural;
                ShowTime();
            });
        };
        _player.Source = MediaSource.CreateFromUri(new Uri(video.Path));

        _playbackTimer = DispatcherQueue.CreateTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(30);
        _playbackTimer.Tick += (_, _) => OnPlaybackTick();

        PlayButton.Click += (_, _) => TogglePlayback();
        SetPlaying(false);
        TimelineCanvas.Draw += TimelineCanvas_Draw;
        TimelineCanvas.PointerPressed += Timeline_PointerPressed;
        TimelineCanvas.PointerMoved += Timeline_PointerMoved;
        TimelineCanvas.PointerReleased += Timeline_PointerReleased;
        TimelineCanvas.PointerCaptureLost += (_, _) => _timelineDrag = TimelineDrag.None;
        TimelineCanvas.SizeChanged += (_, _) => TimelineCanvas.Invalidate();

        InitializeVideoOptions(video);
        CancelExportButton.Click += (_, _) => _export?.Cancel();
        AppWindow.Closing += VideoWindow_Closing;
        ShowTime();
        _ = LoadThumbnailsAsync(video.Path);
    }

    /// <summary>Anything that changes the result: the next close asks before throwing it away.</summary>
    private void MarkChanged() => _unsaved = true;

    // ---- Playback ----------------------------------------------------------------------

    /// <summary>Copies each frame the player has into the bottom layer (on the player's thread).</summary>
    private void OnVideoFrame(MediaPlayer sender, object args)
    {
        var target = _frameTarget;
        if (target is null)
            return;
        try
        {
            // The device lock keeps the copy from landing mid-draw.
            using (CanvasDevice.GetSharedDevice().Lock())
                sender.CopyFrameToVideoSurface(target);
        }
        catch (Exception ex)
        {
            Log.Write($"Video frame failed: {ex.Message}");
            return;
        }
        if (_frameQueued)
            return;
        _frameQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _frameQueued = false;
            if (_frameTarget is null)
                return;
            _document.Frame = _frameTarget;
            Canvas.Invalidate();
        });
    }

    private void TogglePlayback()
    {
        if (!IsVideo || _export is not null)
            return;
        if (_playing)
        {
            Pause();
            return;
        }
        // From the end, start over.
        if (_position >= _trimEnd - FrameStep || _position < _trimStart)
            SeekTo(_trimStart);
        _player!.IsMuted = _muted;
        _player.Play();
        SetPlaying(true);
    }

    private void Pause()
    {
        if (!_playing)
            return;
        _player?.Pause();
        SetPlaying(false);
        if (_player is not null)
            _position = _player.PlaybackSession.Position;
        ShowTime();
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        PlayButton.Content = Shell.FluentIcons.Create(playing ? "pause" : "play", filled: true);
        if (playing)
            _playbackTimer?.Start();
        else
            _playbackTimer?.Stop();
    }

    private void SeekTo(TimeSpan time)
    {
        if (_player is null)
            return;
        _position = time < TimeSpan.Zero ? TimeSpan.Zero : time > _length ? _length : time;
        _player.PlaybackSession.Position = _position;
        ShowTime();
    }

    private void StepFrame(int frames)
    {
        Pause();
        SeekTo(Clamp(_position + FrameStep * frames, _trimStart, _trimEnd));
    }

    private void OnPlaybackTick()
    {
        if (_player is null)
            return;
        _position = _player.PlaybackSession.Position;
        if (_position >= _trimEnd)
        {
            // Playback stops at the end of the trim, as the export will.
            Pause();
            SeekTo(_trimEnd);
            return;
        }
        ShowTime();
    }

    private void ShowTime()
    {
        var at = Clamp(_position, _trimStart, _trimEnd) - _trimStart;
        TimeLabel.Text = $"{Format(at)} / {Format(_trimEnd - _trimStart)}";
        TimelineCanvas.Invalidate();
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";

    private static TimeSpan Clamp(TimeSpan t, TimeSpan min, TimeSpan max) => t < min ? min : t > max ? max : t;

    // ---- Timeline ----------------------------------------------------------------------

    /// <summary>A few frames along the recording for the strip; each is the keyframe nearest its time.</summary>
    private async Task LoadThumbnailsAsync(string path)
    {
        const int count = 16;
        const float height = 96;
        var length = _length;
        var token = _closing.Token;
        try
        {
            await Task.Run(() =>
            {
                using var reader = new MediaReader(path, audio: false);
                var device = CanvasDevice.GetSharedDevice();
                float width = height * reader.Width / reader.Height;
                for (int i = 0; i < count && !token.IsCancellationRequested; i++)
                {
                    reader.Seek(length * ((i + 0.5) / count));
                    if (reader.Read() is not { } frame)
                        break;
                    using var full = CanvasBitmap.CreateFromBytes(device, frame.Data, reader.Width, reader.Height,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                    var thumbnail = new CanvasRenderTarget(device, width, height, 96);
                    using (var ds = thumbnail.CreateDrawingSession())
                        ds.DrawImage(full, new Rect(0, 0, width, height), full.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            thumbnail.Dispose();
                            return;
                        }
                        _thumbnails.Add(thumbnail);
                        TimelineCanvas.Invalidate();
                    });
                }
            }, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write($"Timeline thumbnails failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Rect Strip => new(StripInset, 2, Math.Max(1, TimelineCanvas.ActualWidth - 2 * StripInset), Math.Max(1, TimelineCanvas.ActualHeight - 4));

    private float XAt(TimeSpan t)
    {
        var strip = Strip;
        return (float)(strip.X + strip.Width * (_length.Ticks == 0 ? 0 : t / _length));
    }

    private TimeSpan TimeAt(double x)
    {
        var strip = Strip;
        double fraction = Math.Clamp((x - strip.X) / strip.Width, 0, 1);
        return _length * fraction;
    }

    private void TimelineCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var strip = Strip;
        float radius = 6;
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        ds.FillRoundedRectangle(strip, radius, radius, dark ? Color.FromArgb(255, 45, 45, 45) : Color.FromArgb(255, 225, 225, 225));

        // Frames across the strip, each tile showing the thumbnail nearest its time.
        if (_thumbnails.Count > 0)
        {
            using var clip = CanvasGeometry.CreateRoundedRectangle(sender, strip, radius, radius);
            using (ds.CreateLayer(1, clip))
            {
                var first = _thumbnails[0];
                float tileWidth = (float)(strip.Height * first.Size.Width / first.Size.Height);
                int tiles = (int)Math.Ceiling(strip.Width / tileWidth);
                for (int k = 0; k < tiles; k++)
                {
                    double center = (k + 0.5) * tileWidth / strip.Width;
                    int index = Math.Clamp((int)(center * 16), 0, _thumbnails.Count - 1);
                    var image = _thumbnails[index];
                    ds.DrawImage(image, new Rect(strip.X + k * tileWidth, strip.Y, tileWidth, strip.Height), image.Bounds);
                }
            }
        }

        // Trimmed away: dimmed.
        float start = XAt(_trimStart), end = XAt(_trimEnd);
        var dim = Color.FromArgb(170, 0, 0, 0);
        if (start > strip.X)
            ds.FillRectangle(new Rect(strip.X, strip.Y, start - strip.X, strip.Height), dim);
        if (end < strip.Right)
            ds.FillRectangle(new Rect(end, strip.Y, strip.Right - end, strip.Height), dim);

        // The kept part: framed, with a handle at each end.
        var accent = AccentColor;
        ds.DrawRoundedRectangle(new Rect(start, strip.Y + 1, Math.Max(1, end - start), strip.Height - 2), 4, 4, accent, 3);
        foreach (float x in new[] { start, end - HandleWidth })
        {
            var handle = new Rect(x, strip.Y, HandleWidth, strip.Height);
            ds.FillRoundedRectangle(handle, 4, 4, accent);
            float mid = (float)(handle.X + HandleWidth / 2);
            ds.DrawLine(mid, (float)(strip.Y + strip.Height * 0.32), mid, (float)(strip.Y + strip.Height * 0.68), Color.FromArgb(230, 255, 255, 255), 2);
        }

        // The playhead.
        float head = XAt(Clamp(_position, TimeSpan.Zero, _length));
        ds.DrawLine(head, (float)strip.Y - 1, head, (float)strip.Bottom + 1, Color.FromArgb(160, 0, 0, 0), 4);
        ds.DrawLine(head, (float)strip.Y - 1, head, (float)strip.Bottom + 1, Color.FromArgb(255, 255, 255, 255), 2);
    }

    private void Timeline_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_export is not null)
            return;
        float x = (float)e.GetCurrentPoint(TimelineCanvas).Position.X;
        float start = XAt(_trimStart), end = XAt(_trimEnd);
        _timelineDrag = Math.Abs(x - (start + HandleWidth / 2)) <= HandleWidth ? TimelineDrag.TrimStart
            : Math.Abs(x - (end - HandleWidth / 2)) <= HandleWidth ? TimelineDrag.TrimEnd
            : TimelineDrag.Scrub;
        Pause();
        TimelineCanvas.CapturePointer(e.Pointer);
        DragTimeline(x);
        e.Handled = true;
    }

    private void Timeline_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        float x = (float)e.GetCurrentPoint(TimelineCanvas).Position.X;
        if (_timelineDrag != TimelineDrag.None)
        {
            DragTimeline(x);
            return;
        }
        // Show that the ends can be dragged.
        float start = XAt(_trimStart), end = XAt(_trimEnd);
        bool onHandle = Math.Abs(x - (start + HandleWidth / 2)) <= HandleWidth || Math.Abs(x - (end - HandleWidth / 2)) <= HandleWidth;
        TimelineHost.Cursor = onHandle ? _resizeWeCursor : null;
    }

    private void Timeline_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_timelineDrag is TimelineDrag.TrimStart or TimelineDrag.TrimEnd)
            Log.Write($"Trimmed to {_trimStart.TotalSeconds:F2}–{_trimEnd.TotalSeconds:F2} s");
        _timelineDrag = TimelineDrag.None;
        TimelineCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void DragTimeline(float x)
    {
        var t = TimeAt(x);
        switch (_timelineDrag)
        {
            case TimelineDrag.TrimStart:
                _trimStart = Clamp(t, TimeSpan.Zero, _trimEnd - MinTrimLength);
                MarkChanged();
                SeekTo(_trimStart);
                break;
            case TimelineDrag.TrimEnd:
                _trimEnd = Clamp(t, _trimStart + MinTrimLength, _length);
                MarkChanged();
                SeekTo(_trimEnd);
                break;
            case TimelineDrag.Scrub:
                SeekTo(Clamp(t, _trimStart, _trimEnd));
                break;
        }
    }

    // ---- Video options -----------------------------------------------------------------

    private static readonly (string Label, int Height)[] VideoSizes = [("Original", 0), ("1080p", 1080), ("720p", 720), ("480p", 480)];

    private void InitializeVideoOptions(RecordedVideo video)
    {
        SoundButton.IsChecked = video.HasAudio;
        SoundButton.Click += (_, _) =>
        {
            _muted = SoundButton.IsChecked != true;
            if (_player is not null)
                _player.IsMuted = _muted;
            ShowSoundButton();
            MarkChanged();
        };

        VideoFormatPicker.Items.Add("MP4");
        VideoFormatPicker.Items.Add("GIF");
        VideoFormatPicker.SelectedIndex = Settings.Current.Video.Format.Equals("GIF", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        // GIFs have no sound.
        SoundButton.IsEnabled = video.HasAudio && VideoFormatPicker.SelectedIndex == 0;
        ShowSoundButton();
        VideoFormatPicker.SelectionChanged += (_, _) =>
        {
            Settings.Current.Video.Format = VideoFormatPicker.SelectedIndex == 1 ? "GIF" : "MP4";
            SoundButton.IsEnabled = video.HasAudio && VideoFormatPicker.SelectedIndex == 0; // GIFs have no sound
            ShowSoundButton();
        };

        foreach (var (label, _) in VideoSizes)
            VideoSizePicker.Items.Add(label);
        VideoSizePicker.SelectedIndex = Math.Max(0, Array.FindIndex(VideoSizes, s => s.Height == Settings.Current.Video.MaxHeight));
        VideoSizePicker.SelectionChanged += (_, _) =>
        {
            if (VideoSizePicker.SelectedIndex >= 0)
                Settings.Current.Video.MaxHeight = VideoSizes[VideoSizePicker.SelectedIndex].Height;
        };
    }

    private void ShowSoundButton()
    {
        bool on = SoundButton.IsChecked == true && SoundButton.IsEnabled;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(Shell.FluentIcons.Create(on ? "speaker_2" : "speaker_mute", filled: false));
        content.Children.Add(new TextBlock { Text = _video?.HasAudio == true ? "Sound" : "No sound", VerticalAlignment = VerticalAlignment.Center });
        SoundButton.Content = content;
    }

    private bool ExportsGif => VideoFormatPicker.SelectedIndex == 1;

    // ---- Exporting, saving and copying -------------------------------------------------

    /// <summary>Writes the trimmed, annotated video to <paramref name="path"/>, showing progress; false if cancelled or it failed.</summary>
    private async Task<bool> ExportVideoAsync(string path, bool gif)
    {
        if (_video is null || _export is not null)
            return false;
        FinishEditing();
        Pause();
        var options = new VideoExport.Options(gif, Settings.Current.Video.MaxHeight, _muted, _trimStart, _trimEnd);
        _export = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        SetExporting(true, gif);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var progress = new Progress<double>(p =>
            {
                ExportProgress.Value = p;
                ExportLabel.Text = $"Making the {(gif ? "GIF" : "video")}… {p:P0}";
            });
            await VideoExport.WriteAsync(_document, _video, options, path, progress, _export.Token);
            Log.Write($"Exported {path} ({new FileInfo(path).Length / 1e6:F1} MB) in {clock.Elapsed.TotalSeconds:F1} s");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Write("Export cancelled");
            return false;
        }
        catch (Exception ex)
        {
            Log.Write($"Export to {path} failed: {ex}");
            ShowMessage($"Couldn't make the {(gif ? "GIF" : "video")}");
            return false;
        }
        finally
        {
            _export.Dispose();
            _export = null;
            SetExporting(false, gif);
        }
    }

    private void SetExporting(bool exporting, bool gif)
    {
        ExportOverlay.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        ExportProgress.Value = 0;
        ExportLabel.Text = $"Making the {(gif ? "GIF" : "video")}…";
        Toolbar.IsEnabled = OptionsBar.IsEnabled = !exporting;
        CanvasHost.IsHitTestVisible = !exporting;
        Timeline.IsHitTestVisible = !exporting;
        PlayButton.IsEnabled = !exporting;
    }

    private string VideoName => $"Windshot {DateTime.Now:yyyy-MM-dd HHmmss}";

    /// <summary>Save (Ctrl+S) for a recording: straight to the videos folder, or asking where.</summary>
    private async Task<bool> SaveVideoAsync(bool ask)
    {
        bool gif = ExportsGif;
        string path;
        if (ask)
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.VideosLibrary, SuggestedFileName = VideoName };
            // The first choice is the default.
            if (gif)
                picker.FileTypeChoices.Add("GIF animation", new List<string> { ".gif" });
            picker.FileTypeChoices.Add("MP4 video", new List<string> { ".mp4" });
            if (!gif)
                picker.FileTypeChoices.Add("GIF animation", new List<string> { ".gif" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return false;
            path = file.Path;
            gif = file.FileType.Equals(".gif", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            string folder = Settings.Current.Video.EffectiveFolder;
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Save to {folder} failed: {ex.Message}");
                ShowMessage($"Couldn't save to {folder}. Use Save as (Ctrl+Shift+S).");
                return false;
            }
            path = UniquePath(folder, VideoName, gif ? ".gif" : ".mp4");
        }

        if (!await ExportVideoAsync(path, gif))
            return false;
        _unsaved = false;
        string message = ask ? $"Saved {Path.GetFileName(path)}" : $"Saved to {Path.GetFileName(Path.GetDirectoryName(path))}";
        if (Settings.Current.Editor.CloseAfterSaveOrCopy)
            CloseAfterExport(message);
        else
            ShowMessage(message);
        return true;
    }

    /// <summary>Copy for a recording: the video as a file, which pastes into chats, emails and folders.</summary>
    private async Task CopyVideoAsync()
    {
        bool gif = ExportsGif;
        string folder = Path.Combine(ScreenRecording.Folder, "Copies");
        Directory.CreateDirectory(folder);
        string path = UniquePath(folder, VideoName, gif ? ".gif" : ".mp4");
        if (!await ExportVideoAsync(path, gif))
            return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems([file]);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            _unsaved = false;
            CloseAfterExport(gif ? "Copied the GIF" : "Copied the video");
            if (!Settings.Current.Editor.CloseAfterSaveOrCopy)
                ShowMessage(gif ? "Copied the GIF" : "Copied the video");
        }
        catch (Exception ex)
        {
            Log.Write($"Copying the video failed: {ex}");
            ShowMessage("Couldn't copy: the clipboard is busy. Try again.");
        }
    }

    // ---- Closing -----------------------------------------------------------------------

    /// <summary>Losing a recording hurts more than losing a screenshot: ask first.</summary>
    private async void VideoWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
            return;
        if (_export is not null)
        {
            args.Cancel = true;
            ShowMessage("Still making the video. Cancel it first.");
            return;
        }
        if (!_unsaved)
            return;
        args.Cancel = true;
        Pause();
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Save this recording?",
            Content = "If you close without saving, the recording is gone.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = Root.ActualTheme,
        };
        _theming.Style(dialog);
        try
        {
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary)
            {
                if (!await SaveVideoAsync(ask: !Settings.Current.Saving.SaveWithoutAsking))
                    return;
            }
            else if (choice != ContentDialogResult.Secondary)
            {
                return;
            }
            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Write($"Close prompt failed: {ex}");
        }
    }

    /// <summary>When the window closes: stop playing, let go of the frames, delete the recording.</summary>
    private void EndVideo()
    {
        if (_video is null)
            return;
        _closing.Cancel();
        _export?.Cancel();
        _playbackTimer?.Stop();
        if (_player is not null)
        {
            _player.VideoFrameAvailable -= OnVideoFrame;
            _player.Dispose();
            _player = null;
        }
        _document.Frame = null;
        _frameTarget?.Dispose();
        _frameTarget = null;
        foreach (var thumbnail in _thumbnails)
            thumbnail.Dispose();
        _thumbnails.Clear();
        try
        {
            File.Delete(_video.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't delete the recording (it goes at the next start): {ex.Message}");
        }
    }
}
