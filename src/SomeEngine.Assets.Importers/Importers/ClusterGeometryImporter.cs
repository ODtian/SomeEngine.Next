using SharpGLTF.Schema2;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Importers;

[AssetImporter("ce474674-bd5e-4ae0-b72e-2d50d64e6413", typeof(ClusterGeometryBlock), typeof(ClusterVertexAttributesBlock), typeof(ClusterMeshBlock),
    SettingsProvider = typeof(GltfMeshSettingsProvider), VersionInputs = new[] { typeof(ModelRoot), typeof(MeshOptimizer.Meshopt) })]
public sealed class ClusterGeometryImporter : IAssetImporter<GltfMesh, GltfMeshSettings>
{
    public async ValueTask ImportAsync(GltfMesh source, GltfMeshSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        VertexStreamsBlock streams = await context.GetBlockAsync<VertexStreamsBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        if (!streams.Primitives.Any(primitive => !primitive.Indices.IsEmpty)) return;
        ClusterGeometryBlock geometry = ClusterBuilder.BuildGeometry(streams, cancellationToken);
        try { output.SetBlock(geometry); }
        catch { geometry.Dispose(); throw; }
        ClusterVertexAttributesBlock attributes = ClusterVertexAttributesEncoder.BuildAttributes(streams, geometry, cancellationToken);
        try { output.SetBlock(attributes); }
        catch { attributes.Dispose(); throw; }
        ClusterMeshBlock pages = ClusterBuilder.BuildMeshBlock(geometry, attributes);
        try { output.SetBlock(pages); }
        catch { pages.Dispose(); throw; }
    }
}
