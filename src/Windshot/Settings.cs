using System.Text.Json;
using System.Text.Json.Serialization;
using Windshot.Editing;

namespace Windshot;

/// <summary>
/// User preferences, stored as hand-editable JSON in %LOCALAPPDATA%\Windshot\settings.json.
/// Comments and trailing commas are allowed; missing values fall back to defaults.
/// </summary>
internal sealed class Settings
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Windshot", "settings.json");

    public static Settings Current { get; private set; } = new();

    /// <summary>When Windshot last wrote the file, so the file watcher can ignore our own saves.</summary>
    public static DateTime LastSavedUtc { get; private set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Keep "Ctrl+Shift+2" readable instead of escaping the plus signs.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public HotkeySettings Hotkeys { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();

    public sealed class CaptureSettings
    {
        /// <summary>Show the magnifier (with the color under the cursor) on the capture overlay. M toggles it.</summary>
        public bool ShowMagnifier { get; set; } = true;
    }
    public BeautifySettings Beautify { get; set; } = new();
    public ClipboardSettings Clipboard { get; set; } = new();

    public sealed class ClipboardSettings
    {
        /// <summary>Leave copies out of Win+V clipboard history and cloud clipboard sync.</summary>
        public bool KeepOutOfHistory { get; set; }
    }

    public sealed class HotkeySettings
    {
        /// <summary>Capture a region and open the editor. E.g. "Ctrl+Shift+2", "Alt+S" or "PrintScreen".</summary>
        public string Capture { get; set; } = "Ctrl+Shift+2";

        /// <summary>Capture a region and copy its text.</summary>
        public string CopyText { get; set; } = "Ctrl+Shift+3";

        /// <summary>Pick an area and scroll it to capture more than fits on screen. Press again to finish.</summary>
        public string ScrollingCapture { get; set; } = "Ctrl+Shift+4";
    }

    public sealed class EditorSettings
    {
        /// <summary>Size (1–10) each tool draws at.</summary>
        public Dictionary<Tool, int> Sizes { get; set; } = new()
        {
            [Tool.Arrow] = 4,
            [Tool.Rectangle] = 3,
            [Tool.Text] = 4,
            [Tool.Blur] = 4,
            [Tool.Step] = 4,
            [Tool.Spotlight] = 4,
            [Tool.Highlighter] = 4,
        };

        public string Color { get; set; } = Palette.Colors[0].Color.ToHex();
        public string HighlighterColor { get; set; } = Palette.Colors[2].Color.ToHex();
        public bool Pixelate { get; set; } = true;
        public bool TextBackground { get; set; }

        /// <summary>Close the editor once its image has been copied or saved.</summary>
        public bool CloseAfterSaveOrCopy { get; set; } = true;

        /// <summary>Show the layers panel; remembered from the last editor.</summary>
        public bool ShowLayers { get; set; }
    }

    public sealed class BeautifySettings
    {
        public bool OnByDefault { get; set; }

        /// <summary>One of the preset names, e.g. "Sky" or "Sunset".</summary>
        public string Preset { get; set; } = BackdropPresets.All[0].Name;

        public double Padding { get; set; } = 64;
    }

    /// <summary>
    /// The app used to be called Winshot. Bring settings over from its folder once, so the
    /// rename doesn't reset anyone's preferences.
    /// </summary>
    private static void MigrateFromOldName()
    {
        string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winshot", "settings.json");
        if (File.Exists(FilePath) || !File.Exists(old))
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.Copy(old, FilePath);
            Log.Write($"Copied settings from {old}");
        }
        catch (IOException ex)
        {
            Log.Write($"Couldn't copy old settings: {ex.Message}");
        }
    }

    public static void Load()
    {
        MigrateFromOldName();
        if (!File.Exists(FilePath))
        {
            Current = new Settings();
            Save(); // write the defaults so there's something to edit
            return;
        }

        try
        {
            Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
            Current.FillMissing();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Write($"Couldn't read settings, using defaults: {ex.Message}");
            // Keep the broken file, so hand edits aren't lost when defaults are saved over it.
            try
            {
                File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            }
            catch (IOException)
            {
            }
            Current = new Settings();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write then swap, so a crash mid-write can't leave a half-written file.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
            LastSavedUtc = DateTime.UtcNow;
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't save settings: {ex.Message}");
        }
    }

    /// <summary>A hand-edited file can null out sections or drop entries; restore defaults for those.</summary>
    private void FillMissing()
    {
        Hotkeys ??= new HotkeySettings();
        Capture ??= new CaptureSettings();
        Editor ??= new EditorSettings();
        Beautify ??= new BeautifySettings();
        Clipboard ??= new ClipboardSettings();

        var sizes = Editor.Sizes ?? [];
        Editor.Sizes = new EditorSettings().Sizes;
        foreach (var (tool, size) in sizes)
            Editor.Sizes[tool] = Math.Clamp(size, Annotation.MinWeight, Annotation.MaxWeight);
    }
}
