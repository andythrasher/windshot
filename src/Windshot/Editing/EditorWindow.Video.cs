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
    private enum TimelineDrag { None, Scrub, TrimStart, TrimEnd, LayerStart, LayerEnd, LayerMove }
    private TimelineDrag _timelineDrag;
    /// <summary>Moving a layer's bar: where along it the pointer took hold.</summary>
    private TimeSpan _layerGrab;
    private bool _layerTimingChanged;

    private const float StripHeight = 50;
    private const float LaneTop = 58;
    private const float LaneHeight = 18;

    private const float StripInset = 10;
    private const float HandleWidth = 10;
    private static readonly TimeSpan MinTrimLength = TimeSpan.FromSeconds(0.5);

    private bool IsVideo => _video is not null;

    private TimeSpan FrameStep => TimeSpan.FromSeconds(1.0 / (_video?.FrameRate ?? 30));

    private void InitializeVideo(RecordedVideo video)
    {
        _video = video;
        _length = _trimEnd = _document.Length = video.Length;
        Title = "Windshot recording";
        Timeline.Visibility = Visibility.Visible;
        ZoomToolButton.Visibility = Visibility.Visible;
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
                    _length = _trimEnd = _document.Length = natural;
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
        TimelineCanvas.PointerCaptureLost += (_, _) =>
        {
            _timelineDrag = TimelineDrag.None;
            Canvas.Invalidate();
        };
        TimelineCanvas.SizeChanged += (_, _) => TimelineCanvas.Invalidate();

        InitializeVideoOptions(video);
        InitializeFades();
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

    private Rect Strip => new(StripInset, 2, Math.Max(1, TimelineCanvas.ActualWidth - 2 * StripInset), StripHeight);

    /// <summary>Under the strip: the selected layer's time.</summary>
    private Rect Lane => new(StripInset, LaneTop, Math.Max(1, TimelineCanvas.ActualWidth - 2 * StripInset), LaneHeight);

    /// <summary>The one layer whose time the lane shows: the selection, if it's a single layer.</summary>
    private Annotation? TimedLayer => _group.Count == 0 ? _selected : null;

    /// <summary>In a recording, the moment shown: where playback is, or the playhead. Null for a screenshot.</summary>
    private TimeSpan? PlayheadTime => !IsVideo ? null : _playing && _player is not null ? _player.PlaybackSession.Position : _position;

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

        DrawLane(ds, sender, dark, accent);

        // The playhead, through the strip and the lane.
        float head = XAt(Clamp(_position, TimeSpan.Zero, _length));
        float bottom = (float)Lane.Bottom + 1;
        ds.DrawLine(head, (float)strip.Y - 1, head, bottom, Color.FromArgb(160, 0, 0, 0), 4);
        ds.DrawLine(head, (float)strip.Y - 1, head, bottom, Color.FromArgb(255, 255, 255, 255), 2);
    }

    /// <summary>The selected layer's bar: when it shows, with its fades as ramps at the ends.</summary>
    private void DrawLane(CanvasDrawingSession ds, CanvasControl sender, bool dark, Color accent)
    {
        var lane = Lane;
        var background = dark ? Color.FromArgb(255, 38, 38, 38) : Color.FromArgb(255, 232, 232, 232);
        ds.FillRoundedRectangle(lane, 4, 4, background);
        var muted = dark ? Color.FromArgb(255, 150, 150, 150) : Color.FromArgb(255, 110, 110, 110);
        using var small = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
        {
            FontSize = 11,
            FontFamily = "Segoe UI Variable Text",
            VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
        };
        if (TimedLayer is not { } layer)
        {
            small.HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center;
            ds.DrawText(_group.Count > 0 ? "Select one layer to set when it shows" : "Select a layer to set when it shows", lane, muted, small);
            return;
        }

        var end = layer.End ?? _length;
        float x0 = XAt(layer.Start), x1 = Math.Max(x0 + 2, XAt(end));
        var bar = new Rect(x0, lane.Y + 1, x1 - x0, lane.Height - 2);
        using var shape = CanvasGeometry.CreateRoundedRectangle(sender, bar, 4, 4);
        using (ds.CreateLayer(1, shape))
        {
            ds.FillRectangle(bar, Color.FromArgb(150, accent.R, accent.G, accent.B));
            // Fades: the bar ramps up and down across them.
            double span = Math.Max(0.001, (end - layer.Start).TotalSeconds);
            float fadeIn = (float)(bar.Width * Math.Min(layer.FadeIn.Seconds(), span / 2) / span);
            float fadeOut = (float)(bar.Width * Math.Min(layer.FadeOut.Seconds(), span / 2) / span);
            if (fadeIn > 0)
                FillTriangle(ds, sender, new(x0, (float)bar.Y), new(x0 + fadeIn, (float)bar.Y), new(x0, (float)bar.Bottom), background);
            if (fadeOut > 0)
                FillTriangle(ds, sender, new(x1, (float)bar.Y), new(x1 - fadeOut, (float)bar.Y), new(x1, (float)bar.Bottom), background);
        }
        ds.DrawRoundedRectangle(bar, 4, 4, accent, 1.5f);
        foreach (float x in new[] { x0 + 3, x1 - 3 })
            ds.DrawLine(x, (float)bar.Y + 4, x, (float)bar.Bottom - 4, Color.FromArgb(230, 255, 255, 255), 2);

        // What it is and when, if there's room.
        var text = new Rect(x0 + 10, bar.Y, Math.Max(0, x1 - x0 - 20), bar.Height);
        if (text.Width > 60)
        {
            using (ds.CreateLayer(1, text))
                ds.DrawText($"{LayerName(layer)} · {Format(layer.Start)}–{Format(end)}", text,
                    dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 20, 20, 20), small);
        }
    }

    private static void FillTriangle(CanvasDrawingSession ds, ICanvasResourceCreator creator, Vector2 a, Vector2 b, Vector2 c, Color color)
    {
        using var triangle = CanvasGeometry.CreatePolygon(creator, [a, b, c]);
        ds.FillGeometry(triangle, color);
    }

    /// <summary>What the pointer is over on the timeline: the trim handles, the strip, or the layer's bar.</summary>
    private TimelineDrag HitTimeline(Vector2 p)
    {
        if (p.Y >= LaneTop - 2)
        {
            if (TimedLayer is not { } layer)
                return TimelineDrag.None;
            float x0 = XAt(layer.Start), x1 = XAt(layer.End ?? _length);
            if (Math.Abs(p.X - x0) <= 7)
                return TimelineDrag.LayerStart;
            if (Math.Abs(p.X - x1) <= 7)
                return TimelineDrag.LayerEnd;
            return p.X > x0 && p.X < x1 ? TimelineDrag.LayerMove : TimelineDrag.None;
        }
        float start = XAt(_trimStart), end = XAt(_trimEnd);
        return Math.Abs(p.X - (start + HandleWidth / 2)) <= HandleWidth ? TimelineDrag.TrimStart
            : Math.Abs(p.X - (end - HandleWidth / 2)) <= HandleWidth ? TimelineDrag.TrimEnd
            : TimelineDrag.Scrub;
    }

    private void Timeline_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_export is not null)
            return;
        var p = e.GetCurrentPoint(TimelineCanvas).Position.ToVector2();
        _timelineDrag = HitTimeline(p);
        if (_timelineDrag == TimelineDrag.None)
            return;
        if (_timelineDrag == TimelineDrag.LayerMove)
            _layerGrab = TimeAt(p.X) - TimedLayer!.Start;
        _layerTimingChanged = false;
        Pause();
        TimelineCanvas.CapturePointer(e.Pointer);
        DragTimeline(p.X);
        e.Handled = true;
    }

    private void Timeline_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(TimelineCanvas).Position.ToVector2();
        if (_timelineDrag != TimelineDrag.None)
        {
            DragTimeline(p.X);
            return;
        }
        // Show what can be dragged.
        TimelineHost.Cursor = HitTimeline(p) switch
        {
            TimelineDrag.TrimStart or TimelineDrag.TrimEnd or TimelineDrag.LayerStart or TimelineDrag.LayerEnd => _resizeWeCursor,
            TimelineDrag.LayerMove => _moveCursor,
            _ => null,
        };
    }

    private void Timeline_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_timelineDrag is TimelineDrag.TrimStart or TimelineDrag.TrimEnd)
            Log.Write($"Trimmed to {_trimStart.TotalSeconds:F2}–{_trimEnd.TotalSeconds:F2} s");
        // A layer's time is part of the document, so it's undoable.
        if (_layerTimingChanged)
            Commit();
        _layerTimingChanged = false;
        _timelineDrag = TimelineDrag.None;
        TimelineCanvas.ReleasePointerCapture(e.Pointer);
        Canvas.Invalidate(); // a scrub has ended: the selection shows in full again
    }

    /// <summary>Layers last at least this long, so their bar stays big enough to grab.</summary>
    private static readonly TimeSpan MinLayerLength = TimeSpan.FromSeconds(0.2);

    private void DragTimeline(float x)
    {
        var t = TimeAt(x);
        var layer = TimedLayer;
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
            case TimelineDrag.LayerStart when layer is not null:
                layer.Start = Clamp(t, TimeSpan.Zero, (layer.End ?? _length) - MinLayerLength);
                if (layer.Start < FrameStep)
                    layer.Start = TimeSpan.Zero; // snaps to the very start
                _layerTimingChanged = true;
                SeekTo(layer.Start);
                break;
            case TimelineDrag.LayerEnd when layer is not null:
                var end = Clamp(t, layer.Start + MinLayerLength, _length);
                layer.End = end > _length - FrameStep ? null : end; // to the end, wherever that is
                _layerTimingChanged = true;
                SeekTo((layer.End ?? _length) - FrameStep);
                break;
            case TimelineDrag.LayerMove when layer is not null:
                var length = (layer.End ?? _length) - layer.Start;
                var start = Clamp(t - _layerGrab, TimeSpan.Zero, _length - length);
                bool toEnd = layer.End is null && start + length >= _length - FrameStep;
                layer.Start = start;
                layer.End = toEnd ? null : start + length;
                _layerTimingChanged = true;
                SeekTo(layer.Start);
                break;
        }
        Canvas.Invalidate();
    }

    // ---- Transitions and motion --------------------------------------------------------

    private bool _syncingFades;

    private static readonly (LayerMotion Motion, string Name)[] Motions =
    [
        (LayerMotion.None, "None"), (LayerMotion.ZoomIn, "Zoom in"), (LayerMotion.ZoomOut, "Zoom out"),
        (LayerMotion.PanLeft, "Pan left"), (LayerMotion.PanRight, "Pan right"), (LayerMotion.PanUp, "Pan up"), (LayerMotion.PanDown, "Pan down"),
    ];

    /// <summary>What the In and Out style pickers list for the selection; null is None (at once).</summary>
    private List<Transition?> _styleChoices = new();

    private void InitializeFades()
    {
        foreach (var picker in new[] { InSpeedPicker, OutSpeedPicker })
        {
            picker.Items.Add("Quick");
            picker.Items.Add("Slow");
        }
        EdgePicker.Items.Add("Sharp");
        EdgePicker.Items.Add("Feathered");
        foreach (var (_, name) in Motions)
            MotionPicker.Items.Add(name);
        AnimateIcon.Content = Shell.FluentIcons.Create("timer", filled: false);
        InStylePicker.SelectionChanged += (_, _) => SetTransition(comingIn: true);
        OutStylePicker.SelectionChanged += (_, _) => SetTransition(comingIn: false);
        InSpeedPicker.SelectionChanged += (_, _) => SetTransition(comingIn: true);
        OutSpeedPicker.SelectionChanged += (_, _) => SetTransition(comingIn: false);
        EdgePicker.SelectionChanged += (_, _) =>
        {
            if (_syncingFades || Selection.Count == 0)
                return;
            foreach (var layer in Selection)
                layer.FeatheredWipes = EdgePicker.SelectedIndex == 1;
            Commit();
            Canvas.Invalidate();
        };
        MotionPicker.SelectionChanged += (_, _) => SetMotion(Motions[Math.Max(0, MotionPicker.SelectedIndex)].Motion);
        WholeVideoButton.Click += (_, _) =>
        {
            foreach (var layer in Selection)
            {
                layer.Start = TimeSpan.Zero;
                layer.End = null;
            }
            Commit();
            TimelineCanvas.Invalidate();
            Canvas.Invalidate();
        };
    }

    /// <summary>Applies the In or Out pickers (style and speed) to the selection.</summary>
    private void SetTransition(bool comingIn)
    {
        if (_syncingFades || Selection.Count == 0)
            return;
        var stylePicker = comingIn ? InStylePicker : OutStylePicker;
        var speedPicker = comingIn ? InSpeedPicker : OutSpeedPicker;
        var style = stylePicker.SelectedIndex > 0 ? _styleChoices[stylePicker.SelectedIndex] : null;
        var length = style is null ? FadeLength.None : speedPicker.SelectedIndex == 1 ? FadeLength.Slow : FadeLength.Quick;
        foreach (var layer in Selection)
        {
            if (comingIn)
            {
                layer.FadeIn = length;
                if (style is { } s)
                    layer.InStyle = s;
            }
            else
            {
                layer.FadeOut = length;
                if (style is { } s)
                    layer.OutStyle = s;
            }
        }
        Commit();
        SyncFades();
        Canvas.Invalidate();
    }

    private void SetMotion(LayerMotion motion)
    {
        if (_syncingFades || Selection.Count == 0)
            return;
        foreach (var layer in Selection.Where(a => a.CanMove))
            layer.Motion = motion;
        Commit();
        Canvas.Invalidate();
    }

    /// <summary>
    /// The styles a layer can come in and go out with: a zoom just zooms, spotlights (which dim
    /// as one) can only fade, and other effects can't pop.
    /// </summary>
    private static IEnumerable<Transition> StylesFor(Annotation layer) => layer switch
    {
        ZoomAnnotation or SpotlightAnnotation => [Transition.Fade],
        { IsEffect: true } => Transitions.All.Select(t => t.Style).Where(t => t != Transition.Pop),
        _ => Transitions.All.Select(t => t.Style),
    };

    /// <summary>
    /// Shows the selection's transitions and motion (the first layer's, for several); hidden for
    /// a screenshot or no selection. For a zoom, they're how it zooms in and out.
    /// </summary>
    private void SyncFades()
    {
        bool show = IsVideo && Selection.Count > 0;
        Show(FadeContainer, show);
        if (show)
        {
            bool zoom = Selection.All(a => a is ZoomAnnotation);
            InHeader.Text = zoom ? "Zoom in" : "In";
            OutHeader.Text = zoom ? "Zoom out" : "Out";
            AnimateLabel.Text = zoom ? "Zoom" : "Animate";
            Show(MotionPicker, Selection.Any(a => a.CanMove));

            _syncingFades = true;
            // The styles every selected layer can do.
            var styles = Selection.Select(StylesFor).Aggregate((a, b) => a.Intersect(b)).ToList();
            var choices = new List<Transition?> { null };
            choices.AddRange(styles.Select(s => (Transition?)s));
            if (!choices.SequenceEqual(_styleChoices))
            {
                _styleChoices = choices;
                foreach (var picker in new[] { InStylePicker, OutStylePicker })
                {
                    picker.Items.Clear();
                    picker.Items.Add("None");
                    foreach (var style in styles)
                        picker.Items.Add(zoom ? "Smooth" : Transitions.All.First(t => t.Style == style).Name);
                }
            }
            var first = Selection[0];
            InStylePicker.SelectedIndex = first.FadeIn == FadeLength.None ? 0 : Math.Max(1, _styleChoices.IndexOf(first.InStyle));
            OutStylePicker.SelectedIndex = first.FadeOut == FadeLength.None ? 0 : Math.Max(1, _styleChoices.IndexOf(first.OutStyle));
            InSpeedPicker.SelectedIndex = first.FadeIn == FadeLength.Slow ? 1 : 0;
            OutSpeedPicker.SelectedIndex = first.FadeOut == FadeLength.Slow ? 1 : 0;
            InSpeedPicker.IsEnabled = first.FadeIn != FadeLength.None;
            OutSpeedPicker.IsEnabled = first.FadeOut != FadeLength.None;
            Show(EdgePicker, (first.FadeIn != FadeLength.None && first.InStyle.IsWipe() && _styleChoices.Contains(first.InStyle))
                || (first.FadeOut != FadeLength.None && first.OutStyle.IsWipe() && _styleChoices.Contains(first.OutStyle)));
            EdgePicker.SelectedIndex = first.FeatheredWipes ? 1 : 0;
            var moving = Selection.FirstOrDefault(a => a.CanMove);
            MotionPicker.SelectedIndex = moving is null ? 0 : Array.FindIndex(Motions, m => m.Motion == moving.Motion);
            _syncingFades = false;
        }
        if (IsVideo)
            TimelineCanvas.Invalidate();
    }

    /// <summary>Selecting a layer that isn't showing at the playhead moves the playhead into it, so you can see it.</summary>
    private void ShowSelectedLayer()
    {
        // (Within its time counts, even at the very start of a fade: a new layer stays where it was put.)
        if (!IsVideo || TimedLayer is not { } layer || (_position >= layer.Start && _position <= (layer.End ?? _length)))
            return;
        Pause();
        var into = TimeSpan.FromSeconds(Math.Min(1, ((layer.End ?? _length) - layer.Start).TotalSeconds / 2));
        SeekTo(Clamp(layer.Start + into, _trimStart, _trimEnd));
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
