using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.RenderGraph.Diagnostics;

public static class RenderGraphDiagnosticsDataJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(RenderGraphDiagnosticsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return JsonSerializer.Serialize(data, Options);
    }

    public static RenderGraphDiagnosticsData Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        RenderGraphDiagnosticsData data = JsonSerializer.Deserialize<RenderGraphDiagnosticsData>(json, Options)
            ?? throw new JsonException("The Render Graph data is empty.");
        IReadOnlyList<string> errors = RenderGraphDiagnosticsDataValidation.Validate(data);
        if (errors.Count != 0)
            throw new JsonException(string.Join("; ", errors));
        return data;
    }
}
