using System.Text.Json.Serialization;

namespace SomeEngine.Runtime;

// Keep the existing capture contract: public numeric fields and indented JSON.
[JsonSourceGenerationOptions(IncludeFields = true, WriteIndented = true)]
[JsonSerializable(typeof(SceneFrame))]
internal partial class SceneFrameJsonContext : JsonSerializerContext;
