using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.RenderGraph.Diagnostics;

public static class RenderGraphSnapshotJsonSerializer
{
    private static readonly RenderGraphJson Json = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<QueueType>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<GraphPassKind>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<RenderGraphResourceOwnership>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<RenderGraphResourceLifetime>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<Format>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<QueryType>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<GraphAccessTargetKind>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<GraphAccessMode>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<WriteCoverage>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PipelineSync>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<ResourceAccess>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<TextureAspects>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<TextureLayout>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<ResourceContentState>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<BufferContentScope>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<RenderGraphDependencyKind>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<RenderGraphBarrierKind>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<BarrierPhase>(JsonNamingPolicy.CamelCase),
        },
    });

    public static string Serialize(RenderGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, Json.RenderGraphSnapshot);
    }

    public static RenderGraphSnapshot Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        RenderGraphSnapshot snapshot = JsonSerializer.Deserialize(json, Json.RenderGraphSnapshot)
            ?? throw new JsonException("The Render Graph snapshot is empty.");
        IReadOnlyList<string> errors = RenderGraphSnapshotValidation.Validate(snapshot);
        if (errors.Count != 0)
            throw new JsonException(string.Join("; ", errors));
        return snapshot;
    }
}

[JsonSerializable(typeof(RenderGraphSnapshot))]
internal partial class RenderGraphJson : JsonSerializerContext;
