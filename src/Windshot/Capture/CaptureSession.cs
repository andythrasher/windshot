using System.Drawing;
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
    public static void Begin(Action<CapturedImage> onCaptured, string? hint = null) =>
        Show(hint, (snapshot, area, scale, window) =>
        {
            var image = snapshot.Crop(area, scale);
            // A clicked window: leave out what shows through its rounded corners.
            if (window is WindowRegion w)
                CornerMask.Apply(image, w.Bounds, w.CornerRadius(scale));
            return () => onCaptured(image);
        });

    /// <summary>Lets the user pick an area (or click a window) without capturing it; for live captures.</summary>
    /// <param name="onSelected">The area in desktop pixels, and its monitor's scale.</param>
    public static void SelectArea(Action<Rectangle, double> onSelected, string? hint = null) =>
        Show(hint, (_, area, scale, _) => () => onSelected(area, scale));

    /// <param name="prepare">
    /// Runs while the frozen snapshot is still around; returns what to do once the overlays are gone.
    /// </param>
    private static void Show(string? hint, Func<ScreenSnapshot, Rectangle, double, WindowRegion?, Action> prepare)
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
            overlay.Selected += (source, desktopRect, window) =>
            {
                Log.Write($"Selected {desktopRect} at scale {source.MonitorScale}" + (window is { } w ? $", a window with {w.Corners} corners" : ""));
                var then = prepare(snapshot, desktopRect, source.MonitorScale, window);
                End();
                then();
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
