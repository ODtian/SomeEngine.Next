using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.RenderGraph.Diagnostics;

public static class RenderGraphSnapshotJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
            ?? throw new JsonException("The Render Graph snapshot is empty.");
        IReadOnlyList<string> errors = RenderGraphSnapshotValidation.Validate(snapshot);
        if (errors.Count != 0)
            throw new JsonException(string.Join("; ", errors));
        return snapshot;
    }
}
