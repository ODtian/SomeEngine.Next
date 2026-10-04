using SharpGLTF.Schema2;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Importers;

[AssetImporter("ce474674-bd5e-4ae0-b72e-2d50d64e6413", typeof(ClusterGeometryBlock),
    SettingsProvider = typeof(GltfMeshSettingsProvider), VersionInputs = new[] { typeof(ModelRoot), typeof(MeshOptimizer.Meshopt) })]
public sealed class ClusterGeometryImporter : IAssetImporter<GltfMesh, GltfMeshSettings>
{
    public async ValueTask ImportAsync(GltfMesh source, GltfMeshSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        VertexStreamsBlock streams = await context.GetBlockAsync<VertexStreamsBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        ClusterGeometryBlock geometry = ClusterBuilder.BuildGeometry(streams, cancellationToken);
        try { output.SetBlock(geometry); }
        catch { geometry.Dispose(); throw; }
    }
}
