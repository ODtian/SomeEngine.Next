using System.Numerics;
using System.Text.Json.Serialization;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Job;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Importers;

public sealed record GltfMeshSettings(bool GenerateTangents = false);

[JsonSerializable(typeof(GltfMeshSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
public sealed partial class GltfMeshSettingsJson : JsonSerializerContext;

public sealed class GltfMeshSettingsProvider : IAssetImporterSettings<GltfMeshSettings>
{
    public GltfMeshSettings DefaultSettings => new();
    public GltfMeshSettings GetDefaults(SourceMeta source) => new(source.GetSettings(GltfSourceImporter.SourceImporterId,
        new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings).GenerateTangents);
    public System.Text.Json.Serialization.Metadata.JsonTypeInfo<GltfMeshSettings> TypeInfo => GltfMeshSettingsJson.Default.GltfMeshSettings;
}

/// <summary>Produces semantic mesh regions directly from the native glTF object.</summary>
[AssetImporter("334ba003-337f-4298-ae72-1a5ce4861591", typeof(MeshBlock), SettingsProvider = typeof(GltfMeshSettingsProvider), VersionInputs = new[] { typeof(SharpGLTF.Schema2.ModelRoot) })]
public sealed class GltfMeshImporter : IAssetImporter<GltfMesh, GltfMeshSettings>
{
    public async ValueTask ImportAsync(GltfMesh source, GltfMeshSettings settings, ImportContext context, ImportOutput output,
        CancellationToken cancellationToken)
    {
        VertexStreamsBlock streams = await context.GetBlockAsync<VertexStreamsBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        var geometry = new MeshBlock();
        try
        {
            for (int index = 0; index < source.Primitives.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var primitive = source.Primitives[index];
                geometry.Regions.Add(new() { Name = $"region_{index}" });
                geometry.RegionSources.Add(new(source.LogicalIndex, index, primitive.Material?.LogicalIndex ?? -1,
                    primitive.Material?.Name ?? $"{source.Name ?? "Mesh"} / primitive {index}"));
            }
            var ranges = new List<PositionRange>();
            foreach (VertexPrimitive primitive in streams.Primitives)
            {
                if (!primitive.Attributes.TryGetValue("POSITION", out VertexStream? positions)) continue;
                for (int start = 0; start < primitive.SourceVertices.Length; start += VertexBatchSize)
                    ranges.Add(new(positions.Values, positions.ComponentCount, start,
                        Math.Min(VertexBatchSize, primitive.SourceVertices.Length - start)));
            }
            if (ranges.Count > 0)
            {
                PositionRange[] positionRanges = ranges.ToArray();
                var bounds = new VertexBounds[positionRanges.Length];
                JobSystem.ExecuteParallel(new ReducePositions(positionRanges, bounds), positionRanges.Length);
                Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
                foreach (VertexBounds range in bounds)
                {
                    minimum = Vector3.Min(minimum, range.Minimum);
                    maximum = Vector3.Max(maximum, range.Maximum);
                }
                Vector3 center = (minimum + maximum) * 0.5f;
                Vector3 extent = maximum - minimum;
                geometry.Bounds = new Bounds { Center = new() { X = center.X, Y = center.Y, Z = center.Z },
                    Radius = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)) * 0.5f };
            }
            output.SetBlock(geometry);
            return;
        }
        catch { geometry.Dispose(); throw; }
    }

    private const int VertexBatchSize = 4096;

    private readonly record struct PositionRange(ReadOnlyMemory<double> Values, int ComponentCount, int Start, int Count);
    private readonly record struct VertexBounds(Vector3 Minimum, Vector3 Maximum);

    private readonly struct ReducePositions(PositionRange[] ranges, VertexBounds[] bounds) : IJobParallelFor
    {
        public void Execute(int index)
        {
            PositionRange range = ranges[index];
            ReadOnlySpan<double> values = range.Values.Span;
            Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
            for (int vertex = range.Start; vertex < range.Start + range.Count; vertex++)
            {
                int row = vertex * range.ComponentCount;
                Vector3 position = new((float)values[row], (float)values[row + 1], (float)values[row + 2]);
                minimum = Vector3.Min(minimum, position);
                maximum = Vector3.Max(maximum, position);
            }
            bounds[index] = new(minimum, maximum);
        }
    }
}
