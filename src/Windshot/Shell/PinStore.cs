using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;
using Windshot.Capture;

namespace Windshot.Shell;

/// <summary>
/// Keeps pins across restarts: each one is a PNG plus a small JSON file with where it is,
/// in %LOCALAPPDATA%\Windshot\pins. Closing a pin deletes its files; quitting Windshot keeps
/// them, and they come back on the next launch.
/// </summary>
internal static class PinStore
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Windshot", "pins");

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

                Bitmap image;
                using (var loaded = new Bitmap(ImagePath(id)))
                    image = new Bitmap(loaded); // detach from the file so it can be deleted later

                var size = new Size(Math.Max(1, (int)(image.Width * state.Zoom)), Math.Max(1, (int)(image.Height * state.Zoom)));
                var location = OnScreen(new Rectangle(new Point(state.X, state.Y), size));
                new PinWindow(image, location, state.Scale, edit, id, state.Zoom, state.Opacity).Show();
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                Log.Write($"Couldn't restore pin {id}: {ex.Message}");
            }
        }
        return restored;
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
