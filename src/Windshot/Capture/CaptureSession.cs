using System.Windows.Forms;

namespace Windshot.Capture;

/// <summary>
/// Freezes the desktop, then shows one selection overlay per monitor. Each overlay lives in
/// that monitor's own physical pixel space, which sidesteps mixed-DPI scaling problems.
/// </summary>
internal static class CaptureSession
{
    private static List<SelectionOverlay>? _overlays;

    /// <param name="hint">Optional instruction shown at the top of each monitor, e.g. for OCR.</param>
    public static void Begin(Action<CapturedImage> onCaptured, string? hint = null)
    {
        if (_overlays is not null)
            return;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = ScreenSnapshot.Take();
        // Taken right after the snapshot, so the clickable windows match the frozen picture.
        var windows = WindowFinder.VisibleWindows();
        Log.Write($"Snapshot {snapshot.VirtualBounds} and {windows.Count} windows in {stopwatch.ElapsedMilliseconds} ms");
        _overlays = new List<SelectionOverlay>();

        void End()
        {
            if (_overlays is null)
                return;
            foreach (var overlay in _overlays)
            {
                overlay.Close();
                overlay.Dispose();
            }
            _overlays = null;
            snapshot.Dispose();
        }

        foreach (var screen in Screen.AllScreens)
        {
            var overlay = new SelectionOverlay(snapshot, screen.Bounds, windows, hint);
            overlay.Selected += (source, desktopRect) =>
            {
                Log.Write($"Selected {desktopRect} at scale {source.MonitorScale}");
                var image = snapshot.Crop(desktopRect, source.MonitorScale);
                End();
                onCaptured(image);
            };
            overlay.Cancelled += End;
            _overlays.Add(overlay);
            overlay.Show();
        }

        // Give keyboard focus to the monitor under the cursor so Esc works immediately.
        _overlays.FirstOrDefault(o => o.Bounds.Contains(Cursor.Position))?.Activate();
        Log.Write($"Overlays shown in {stopwatch.ElapsedMilliseconds} ms: {string.Join(", ", _overlays.Select(o => $"{o.Bounds} visible={o.Visible}"))}");
    }
}
