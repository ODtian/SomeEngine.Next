using SharpGLTF.Schema2;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Importers;

[AssetImporter("084c59d6-3951-4a5e-8fb9-271e965cb948", typeof(MeshDistanceFieldBlock),
    SettingsProvider = typeof(GltfMeshSettingsProvider), VersionInputs = new[] { typeof(ModelRoot) })]
public sealed class MeshDistanceFieldImporter : IAssetImporter<GltfMesh, GltfMeshSettings>
{
    public async ValueTask ImportAsync(GltfMesh source, GltfMeshSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        ClusterGeometryBlock? geometry = await context.TryGetBlockAsync<ClusterGeometryBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        if (geometry is null) return;
        ClusterVertexAttributesBlock attributes = await context.GetBlockAsync<ClusterVertexAttributesBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        MeshDistanceFieldBlock field = MeshDistanceFieldBlock.Build(geometry, attributes, 4, 32, cancellationToken);
        try { output.SetBlock(field); }
        catch { field.Dispose(); throw; }
    }
}
