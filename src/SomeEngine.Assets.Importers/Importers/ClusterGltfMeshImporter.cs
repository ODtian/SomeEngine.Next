using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources.Geometry;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Importers;

/// <summary>Produces only the Cluster representation; another importer may add other representations to the same mesh.</summary>
[AssetImporter("548d394b-5600-49e0-b04a-9c9d0d53b4b0", typeof(ClusterMeshBlock), SettingsProvider = typeof(GltfMeshSettingsProvider), VersionInputs = new[] { typeof(SharpGLTF.Schema2.ModelRoot) })]
public sealed class ClusterGltfMeshImporter : IAssetImporter<GltfMesh, GltfMeshSettings>
{
    public ValueTask ImportAsync(GltfMesh source, GltfMeshSettings settings, ImportContext context, ImportOutput output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using MeshAsset built = ClusterBuilder.ProcessMesh(source, [], source.Name ?? "Mesh",
            new ClusterBuilderOptions { GenerateMissingTangents = settings.GenerateTangents,
                VertexEncoder = StandardClusterVertexEncoder.Instance });
        cancellationToken.ThrowIfCancellationRequested();
        ClusterMeshBlock clusters = built.GetBlock<ClusterMeshBlock>().Duplicate();
        try { output.SetBlock(clusters); }
        catch { clusters.Dispose(); throw; }
        return ValueTask.CompletedTask;
    }
}
