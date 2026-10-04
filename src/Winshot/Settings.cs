using System.Text.Json;
using System.Text.Json.Serialization;
using Winshot.Editing;

namespace Winshot;

/// <summary>
/// User preferences, stored as hand-editable JSON in %LOCALAPPDATA%\Winshot\settings.json.
/// Comments and trailing commas are allowed; missing values fall back to defaults.
/// </summary>
internal sealed class Settings
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winshot", "settings.json");

    public static Settings Current { get; private set; } = new();

    /// <summary>When Winshot last wrote the file, so the file watcher can ignore our own saves.</summary>
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
    public EditorSettings Editor { get; set; } = new();
    public BeautifySettings Beautify { get; set; } = new();

    public sealed class HotkeySettings
    {
        /// <summary>Capture a region and open the editor. E.g. "Ctrl+Shift+2", "Alt+S" or "PrintScreen".</summary>
        public string Capture { get; set; } = "Ctrl+Shift+2";

        /// <summary>Capture a region and copy its text.</summary>
        public string CopyText { get; set; } = "Ctrl+Shift+3";
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
    }

    public sealed class BeautifySettings
    {
        public bool OnByDefault { get; set; }

        /// <summary>One of the preset names, e.g. "Sky" or "Sunset".</summary>
        public string Preset { get; set; } = BackdropPresets.All[0].Name;

        public double Padding { get; set; } = 64;
    }

    public static void Load()
    {
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
        Editor ??= new EditorSettings();
        Beautify ??= new BeautifySettings();

        var sizes = Editor.Sizes ?? [];
        Editor.Sizes = new EditorSettings().Sizes;
        foreach (var (tool, size) in sizes)
            Editor.Sizes[tool] = Math.Clamp(size, Annotation.MinWeight, Annotation.MaxWeight);
    }
}
