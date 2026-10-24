using System.Text.Json.Serialization;

namespace SomeEngine.Diagnostics;

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class TraceJsonContext : JsonSerializerContext;
