using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Windshot.Editing;

/// <summary>
/// Inserting pictures as layers (see <see cref="ImageAnnotation"/>): from the toolbar's Insert
/// image button, pasted with Ctrl+V, or dropped on the canvas. A picture comes in at its own
/// size (one of its pixels to one of the screenshot's) if that fits, or shrunk to fit.
/// </summary>
public sealed partial class EditorWindow
{
    private static readonly string[] ImageFileTypes = [".png", ".jpg", ".jpeg", ".webp"];

    private static bool IsImageFile(IStorageItem item) =>
        item is StorageFile file && ImageFileTypes.Contains(Path.GetExtension(file.Name).ToLowerInvariant());

    private async void InsertImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (var type in ImageFileTypes)
            picker.FileTypeFilter.Add(type);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        StorageFile? file;
        try
        {
            file = await picker.PickSingleFileAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"Image picker failed: {ex.Message}");
            ShowMessage("Couldn't show the file picker. Try pasting the image (Ctrl+V) or dropping it here.");
            return;
        }
        if (file is not null)
            await InsertFileAsync(file, at: null);
    }

    /// <summary>Ctrl+V: a copied picture, or a copied image file.</summary>
    private async void PasteImage()
    {
        try
        {
            var content = Clipboard.GetContent();
            // Browsers and design apps also copy a PNG, which keeps transparency; the plain
            // bitmap doesn't.
            if (content.Contains("PNG") && await content.GetDataAsync("PNG") is IRandomAccessStream png)
            {
                using (png)
                    await InsertImageAsync(png, title: null, at: null);
                return;
            }
            if (content.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await content.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                await InsertImageAsync(stream, title: null, at: null);
                return;
            }
            if (content.Contains(StandardDataFormats.StorageItems) &&
                (await content.GetStorageItemsAsync()).FirstOrDefault(IsImageFile) is StorageFile file)
            {
                await InsertFileAsync(file, at: null);
                return;
            }
            ShowMessage("There's no image on the clipboard to paste.");
        }
        catch (Exception ex)
        {
            // The clipboard can be held open by another app for a moment.
            Log.Write($"Paste failed: {ex.Message}");
            ShowMessage("Couldn't paste: the clipboard is busy. Try again.");
        }
    }

    private void Canvas_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) && !e.DataView.Contains(StandardDataFormats.Bitmap))
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Insert image";
    }

    /// <summary>Dropped image files go in where they're dropped, each a little below and right of the last.</summary>
    private async void Canvas_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var at = ToDocument(e.GetPosition(Canvas).ToVector2());
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var files = (await e.DataView.GetStorageItemsAsync()).Where(IsImageFile).Cast<StorageFile>().ToList();
                if (files.Count == 0)
                    ShowMessage("Windshot can insert PNG, JPEG and WebP images.");
                foreach (var file in files)
                {
                    await InsertFileAsync(file, at);
                    at += new Vector2(24 * Unit);
                }
            }
            else if (e.DataView.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await e.DataView.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                await InsertImageAsync(stream, title: null, at);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Drop failed: {ex.Message}");
            ShowMessage("Couldn't insert what was dropped.");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task InsertFileAsync(StorageFile file, Vector2? at)
    {
        try
        {
            using var stream = await file.OpenReadAsync();
            await InsertImageAsync(stream, file.DisplayName, at);
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't open {file.Path}: {ex.Message}");
            ShowMessage($"Couldn't open {file.Name}.");
        }
    }

    /// <summary>
    /// Adds the picture as a new layer on top, centered on <paramref name="at"/> (or on the
    /// screenshot), at its own size or shrunk to fit the screenshot, and selects it.
    /// </summary>
    private async Task InsertImageAsync(IRandomAccessStream stream, string? title, Vector2? at)
    {
        CanvasBitmap bitmap;
        try
        {
            // 96 DPI: one of its pixels to one image pixel.
            bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), stream, 96);
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't read an inserted image: {ex.Message}");
            ShowMessage("Couldn't read that image. Windshot can insert PNG, JPEG and WebP images.");
            return;
        }

        CommitTextEdit();
        FinishPolygon();
        if (_tool == Tool.Crop)
            SetTool(Tool.Select);
        // What's showing of the screenshot: the crop, if there is one.
        var fit = _document.CropOverride ?? _document.ImageBounds;
        var size = new Vector2(bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height);
        float scale = Math.Min(1, Math.Min((float)fit.Width / size.X, (float)fit.Height / size.Y));
        size *= scale;
        var center = at ?? new Vector2((float)(fit.X + fit.Width / 2), (float)(fit.Y + fit.Height / 2));
        var box = new Rect(center.X - size.X / 2, center.Y - size.Y / 2, size.X, size.Y);

        var image = new ImageAnnotation(bitmap, box, Unit) { Title = title };
        AddLayer(image);
        Select(image);
        Commit();
        Log.Write($"Inserted an image, {bitmap.SizeInPixels.Width} x {bitmap.SizeInPixels.Height}" + (scale < 1 ? $", shrunk to {scale:P0}" : ""));
        Canvas.Invalidate();
    }
}
