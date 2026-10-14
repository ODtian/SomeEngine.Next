using System.Numerics;
using System.Text.Json.Serialization;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
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
    public ValueTask ImportAsync(GltfMesh source, GltfMeshSettings settings, ImportContext context, ImportOutput output,
        CancellationToken cancellationToken)
    {
        var geometry = new MeshBlock();
        try
        {
            Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
            bool hasPositions = false;
            for (int index = 0; index < source.Primitives.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var primitive = source.Primitives[index];
                foreach (Vector3 position in primitive.GetVertexAccessor("POSITION").AsVector3Array())
                {
                    minimum = Vector3.Min(minimum, position);
                    maximum = Vector3.Max(maximum, position);
                    hasPositions = true;
                }
                geometry.Regions.Add(new() { Name = $"region_{index}" });
                geometry.RegionSources.Add(new(source.LogicalIndex, index, primitive.Material?.LogicalIndex ?? -1,
                    primitive.Material?.Name ?? $"{source.Name ?? "Mesh"} / primitive {index}"));
            }
            if (hasPositions)
            {
                Vector3 center = (minimum + maximum) * 0.5f;
                Vector3 extent = maximum - minimum;
                geometry.Bounds = new Bounds { Center = new() { X = center.X, Y = center.Y, Z = center.Z },
                    Radius = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)) * 0.5f };
            }
            output.SetBlock(geometry);
            return ValueTask.CompletedTask;
        }
        catch { geometry.Dispose(); throw; }
    }
}
