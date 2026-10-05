using System.Text.Json.Serialization;

namespace Windshot;

/// <summary>
/// JSON (de)serialization code for settings and pins, generated at compile time. The Release
/// build is trimmed, which leaves out the reflection that JSON would otherwise rely on.
/// Settings makes its own instance with its hand-editing friendly options.
/// </summary>
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(Shell.PinStore.PinState))]
internal sealed partial class JsonContext : JsonSerializerContext;
