namespace SomeEngine.RenderGraph.Diagnostics;

using System.Text.Json;
using System.Text.Json.Serialization;

public static class RenderGraphSnapshotJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(RenderGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, Options);
    }

    public static RenderGraphSnapshot Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        RenderGraphSnapshot snapshot = JsonSerializer.Deserialize<RenderGraphSnapshot>(json, Options)
            ?? throw new InvalidDataException("The JSON document does not contain a render-graph snapshot.");
        return snapshot;
    }
}
