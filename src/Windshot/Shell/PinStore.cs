using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;
using Windshot.Capture;

namespace Windshot.Shell;

/// <summary>
/// Keeps pins across restarts: each one is a PNG plus a small JSON file with where it is,
/// in the pins folder under <see cref="AppPackage.DataFolder"/>. Closing a pin deletes its files; quitting Windshot keeps
/// them, and they come back on the next launch.
/// </summary>
internal static class PinStore
{
    public static string Folder { get; } = Path.Combine(AppPackage.DataFolder, "pins");

    /// <param name="X">Top-left in physical desktop pixels.</param>
    /// <param name="Scale">Device pixels per DIP of the source capture, kept for editing later.</param>
    public sealed record PinState(int X, int Y, float Zoom, double Opacity, double Scale);

    private static string ImagePath(string id) => Path.Combine(Folder, id + ".png");
    private static string StatePath(string id) => Path.Combine(Folder, id + ".json");

    public static void SaveImage(string id, Bitmap image)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string temp = ImagePath(id) + ".tmp";
            image.Save(temp, ImageFormat.Png);
            File.Move(temp, ImagePath(id), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Write($"Couldn't save pin {id}: {ex.Message}");
        }
    }

    public static void SaveState(string id, PinState state)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string temp = StatePath(id) + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, StatePath(id), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't save pin {id}: {ex.Message}");
        }
    }

    public static void Delete(string id)
    {
        foreach (string path in new[] { ImagePath(id), StatePath(id) })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Couldn't delete {path}: {ex.Message}");
            }
        }
    }

    /// <summary>Shows every saved pin again. Pins that would land off every screen move onto the main one.</summary>
    /// <returns>How many pins came back.</returns>
    public static int RestoreAll(Action<CapturedImage> edit)
    {
        if (!Directory.Exists(Folder))
            return 0;

        int restored = 0;
        foreach (string statePath in Directory.GetFiles(Folder, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(statePath);
            try
            {
                var state = JsonSerializer.Deserialize<PinState>(File.ReadAllText(statePath));
                if (state is null || !File.Exists(ImagePath(id)))
                {
                    Delete(id);
                    continue;
                }
                // Values come from a file, so keep them sane: a NaN zoom would make a NaN-sized window.
                float zoom = float.IsFinite(state.Zoom) ? Math.Clamp(state.Zoom, 0.1f, 8f) : 1;
                double opacity = double.IsFinite(state.Opacity) ? state.Opacity : 1;
                double scale = double.IsFinite(state.Scale) && state.Scale is >= 0.5 and <= 8 ? state.Scale : 1;

                if (ImageSize(ImagePath(id)) is not (int width, int height) || width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
                    throw new InvalidDataException("not a PNG Windshot could have saved");

                Bitmap image;
                using (var loaded = new Bitmap(ImagePath(id)))
                    image = new Bitmap(loaded); // detach from the file so it can be deleted later

                var size = new Size(Math.Max(1, (int)(image.Width * zoom)), Math.Max(1, (int)(image.Height * zoom)));
                var location = OnScreen(new Rectangle(new Point(state.X, state.Y), size));
                new PinWindow(image, location, scale, edit, id, zoom, opacity).Show();
                restored++;
            }
            catch (Exception ex)
            {
                // Anything at all (GDI+ reports a corrupt image as out of memory): set the pin
                // aside rather than fail, and fail again, at every start.
                Log.Write($"Couldn't restore pin {id}, set it aside: {ex.Message}");
                SetAside(id);
            }
        }
        return restored;
    }

    /// <summary>Larger than any capture the editor can hold (Direct2D's bitmap limit).</summary>
    private const int MaxSide = 16384;
    private const long MaxPixels = 100_000_000;

    /// <summary>A PNG's width and height from its header, without decoding it (which could need gigabytes).</summary>
    private static (int Width, int Height)? ImageSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var file = File.OpenRead(path);
        if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            return null;
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (!header[..8].SequenceEqual(signature) || !header[12..16].SequenceEqual("IHDR"u8))
            return null;
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>Moves a pin's files into pins\unreadable, so they stop being loaded but aren't lost.</summary>
    private static void SetAside(string id)
    {
        try
        {
            string folder = Path.Combine(Folder, "unreadable");
            Directory.CreateDirectory(folder);
            foreach (string path in new[] { ImagePath(id), StatePath(id) })
            {
                if (File.Exists(path))
                    File.Move(path, Path.Combine(folder, Path.GetFileName(path)), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't set pin {id} aside: {ex.Message}");
        }
    }

    /// <summary>Where to put a pin so enough of it is visible to grab, e.g. after a monitor was unplugged.</summary>
    private static Point OnScreen(Rectangle bounds)
    {
        const int MinVisible = 48;
        foreach (var screen in Screen.AllScreens)
        {
            var visible = Rectangle.Intersect(bounds, screen.WorkingArea);
            if (visible.Width >= Math.Min(MinVisible, bounds.Width) && visible.Height >= Math.Min(MinVisible, bounds.Height))
                return bounds.Location;
        }
        var main = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        return new Point(main.X + MinVisible, main.Y + MinVisible);
    }
}
