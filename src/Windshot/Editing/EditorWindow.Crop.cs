using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;

namespace Windshot.Editing;

/// <summary>
/// The crop tool. While it's active the whole canvas shows, with everything outside the crop
/// dimmed; drag the corners or edges, drag inside to move it, or drag outside to draw a new
/// one. Leaving the tool (or Enter) applies it, Esc cancels. Cropping never discards
/// anything: picking the tool again shows it all, so the crop can be loosened later.
/// </summary>
public sealed partial class EditorWindow
{
    private const float MinCropSize = 8;

    /// <summary>The crop being adjusted, while the crop tool is active (document coordinates).</summary>
    private Rect? _cropRect;
    /// <summary>The crop in effect before the tool was picked, to go back to on Esc.</summary>
    private Rect? _cropBefore;
    private CropDrag? _cropDrag;

    /// <param name="Handle">0–3 corners (clockwise from top-left), 4–7 edges (top, right, bottom, left), -1 moves it, -2 draws a new one.</param>
    private sealed record CropDrag(uint PointerId, int Handle, Vector2 Start, Rect Original);

    private void BeginCrop()
    {
        Select(null);
        _cropBefore = _document.CropOverride;
        _document.CropOverride = null; // show everything there is to crop from
        _cropRect = _cropBefore ?? _document.ContentBounds;
        FitToWindow();
    }

    private void ApplyCrop()
    {
        if (_cropRect is not Rect rect)
            return;
        _cropRect = null;
        _cropDrag = null;

        var whole = _document.ContentBounds; // nothing is cropped while the tool is active
        rect = new Rect(Math.Round(rect.X), Math.Round(rect.Y), Math.Round(rect.Width), Math.Round(rect.Height)).IntersectWith(whole);
        Rect? crop = rect.IsEmpty || rect.Equals(whole) ? null : rect;
        _document.CropOverride = crop;
        if (!Nullable.Equals(crop, _cropBefore))
            Commit();
        FitToWindow();
    }

    private void CancelCrop()
    {
        if (_cropRect is null)
            return;
        _cropRect = null;
        _cropDrag = null;
        _document.CropOverride = _cropBefore;
        FitToWindow();
    }

    private static Vector2[] CropHandles(Rect r)
    {
        float left = (float)r.Left, top = (float)r.Top, right = (float)r.Right, bottom = (float)r.Bottom;
        float cx = (left + right) / 2, cy = (top + bottom) / 2;
        return [new(left, top), new(right, top), new(right, bottom), new(left, bottom),
                new(cx, top), new(right, cy), new(cx, bottom), new(left, cy)];
    }

    private int? HitCropHandle(Rect crop, Vector2 p)
    {
        float reach = (HandleRadius + HitTolerance) / _view.M11;
        var handles = CropHandles(crop);
        for (int i = 0; i < handles.Length; i++)
        {
            if (Vector2.Distance(handles[i], p) <= reach)
                return i;
        }
        return null;
    }

    private void CropPressed(PointerRoutedEventArgs e, Vector2 p)
    {
        if (_cropRect is not Rect crop)
            return;
        // Until something is cropped, the crop is the whole canvas: dragging anywhere draws a new one.
        bool cropped = !crop.Equals(_document.ContentBounds);
        int handle = HitCropHandle(crop, p) ?? (cropped && crop.Contains(p.ToPoint()) ? -1 : -2);
        _cropDrag = new CropDrag(e.Pointer.PointerId, handle, p, crop);
        CanvasHost.CapturePointer(e.Pointer);
    }

    private void CropMoved(Vector2 p)
    {
        if (_cropDrag is not { } drag)
            return;
        var whole = _document.ContentBounds;
        var o = drag.Original;
        double left = o.Left, top = o.Top, right = o.Right, bottom = o.Bottom;
        p = Vector2.Clamp(p, new Vector2((float)whole.Left, (float)whole.Top), new Vector2((float)whole.Right, (float)whole.Bottom));

        switch (drag.Handle)
        {
            case -2: // a new area, from where the drag started
                left = Math.Min(drag.Start.X, p.X);
                right = Math.Max(drag.Start.X, p.X);
                top = Math.Min(drag.Start.Y, p.Y);
                bottom = Math.Max(drag.Start.Y, p.Y);
                break;
            case -1: // move it, staying on the canvas
                double dx = Math.Clamp(p.X - drag.Start.X, whole.Left - o.Left, whole.Right - o.Right);
                double dy = Math.Clamp(p.Y - drag.Start.Y, whole.Top - o.Top, whole.Bottom - o.Bottom);
                left += dx; right += dx; top += dy; bottom += dy;
                break;
            default: // a corner or an edge; it can't pass the opposite side
                int h = drag.Handle;
                if (h is 0 or 3 or 7) left = Math.Min(p.X, right - MinCropSize);
                if (h is 1 or 2 or 5) right = Math.Max(p.X, left + MinCropSize);
                if (h is 0 or 1 or 4) top = Math.Min(p.Y, bottom - MinCropSize);
                if (h is 2 or 3 or 6) bottom = Math.Max(p.Y, top + MinCropSize);
                break;
        }
        _cropRect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        Canvas.Invalidate();
    }

    private void CropReleased(PointerRoutedEventArgs e)
    {
        if (_cropDrag is not { } drag)
            return;
        // A click outside (no real area drawn) leaves the crop as it was.
        if (drag.Handle == -2 && _cropRect is Rect r && (r.Width < MinCropSize || r.Height < MinCropSize))
            _cropRect = drag.Original;
        _cropDrag = null;
        CanvasHost.ReleasePointerCapture(e.Pointer);
        Canvas.Invalidate();
    }

    private void DrawCropOverlay(CanvasDrawingSession ds, Rect crop)
    {
        float px = 1 / _view.M11;
        using (var all = CanvasGeometry.CreateRectangle(ds, _document.Bounds))
        using (var kept = CanvasGeometry.CreateRectangle(ds, crop))
        using (var outside = all.CombineWith(kept, Matrix3x2.Identity, CanvasGeometryCombine.Exclude))
        {
            ds.FillGeometry(outside, Color.FromArgb(150, 0, 0, 0));
        }

        // Thirds, to help line things up, then the edge: white over a dark line so it shows on anything.
        var thirds = Color.FromArgb(90, 255, 255, 255);
        for (int i = 1; i < 3; i++)
        {
            float x = (float)(crop.X + crop.Width * i / 3), y = (float)(crop.Y + crop.Height * i / 3);
            ds.DrawLine(x, (float)crop.Top, x, (float)crop.Bottom, thirds, px);
            ds.DrawLine((float)crop.Left, y, (float)crop.Right, y, thirds, px);
        }
        ds.DrawRectangle(crop, Color.FromArgb(160, 0, 0, 0), 3 * px);
        ds.DrawRectangle(crop, Color.FromArgb(255, 255, 255, 255), 1.5f * px);
        foreach (var handle in CropHandles(crop))
        {
            ds.FillCircle(handle, HandleRadius * px, Color.FromArgb(255, 255, 255, 255));
            ds.DrawCircle(handle, HandleRadius * px, AccentColor, 1.5f * px);
        }

        // The size it will export at, just below the bottom-right corner.
        using var format = new CanvasTextFormat { FontSize = 12 * px, FontFamily = "Segoe UI", WordWrapping = CanvasWordWrapping.NoWrap };
        using var label = new CanvasTextLayout(ds, $"{Math.Round(crop.Width)} × {Math.Round(crop.Height)}", format, 0, 0);
        var size = label.LayoutBounds;
        var at = new Vector2((float)(crop.Right - size.Width - 8 * px), (float)crop.Bottom + 8 * px);
        ds.FillRoundedRectangle(new Rect(at.X - 6 * px, at.Y - 3 * px, size.Width + 12 * px, size.Height + 6 * px), 4 * px, 4 * px, Color.FromArgb(220, 32, 32, 32));
        ds.DrawTextLayout(label, at, Color.FromArgb(255, 255, 255, 255));
    }

    /// <summary>Resize arrows over the handles, a move cursor inside, and a crosshair to draw a new area outside.</summary>
    private InputCursor CropCursor(Vector2 p)
    {
        if (_cropRect is not Rect crop)
            return CrossCursor;
        return HitCropHandle(crop, p) switch
        {
            0 or 2 => _resizeNwSeCursor,
            1 or 3 => _resizeNeSwCursor,
            4 or 6 => _resizeNsCursor,
            5 or 7 => _resizeWeCursor,
            _ => crop.Contains(p.ToPoint()) && !crop.Equals(_document.ContentBounds) ? _moveCursor : CrossCursor,
        };
    }
}
