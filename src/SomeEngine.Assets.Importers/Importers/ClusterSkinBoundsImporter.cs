using System.Numerics;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Importers;

[AssetImporter("5481ec15-2c89-437b-8d26-03cb72937d08", typeof(ClusterSkinBoundsBlock),
    SettingsProvider = typeof(GltfMeshSettingsProvider), VersionInputs = new[] { typeof(ModelRoot) })]
public sealed class ClusterSkinBoundsImporter : IAssetImporter<GltfMesh, GltfMeshSettings>
{
    public static readonly Guid Id = new("5481ec15-2c89-437b-8d26-03cb72937d08");

    public async ValueTask ImportAsync(GltfMesh source, GltfMeshSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        ClusterSkinInfluencesBlock? skin = await context.TryGetBlockAsync<ClusterSkinInfluencesBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        if (skin is null) return;
        ClusterGeometryBlock geometry = await context.GetBlockAsync<ClusterGeometryBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        var clusters = new List<ClusterSkinBoxSet>();
        for (int index = 0; index < geometry.Clusters.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClusterGeometry cluster = geometry.Clusters[index];
            var bounds = new Dictionary<uint, (Vector3 Minimum, Vector3 Maximum)>();
            foreach (ClusterVertexAttribute joints in skin.Clusters[index].Attributes.Where(value => value.Name.StartsWith("JOINTS_", StringComparison.Ordinal)))
            {
                ClusterVertexAttribute weights = skin.Clusters[index].Attributes.Single(value => value.Name == "WEIGHTS_" + joints.Name[7..]);
                for (int vertex = 0; vertex < joints.VertexCount; vertex++)
                for (int influence = 0; influence < joints.ComponentCount; influence++)
                {
                    if (weights.ReadComponent(vertex, influence) <= 0) continue;
                    uint joint = (uint)joints.ReadComponent(vertex, influence);
                    Vector3 position = cluster.DecodePosition(vertex);
                    bounds[joint] = bounds.TryGetValue(joint, out var box)
                        ? (Vector3.Min(box.Minimum, position), Vector3.Max(box.Maximum, position)) : (position, position);
                }
            }
            clusters.Add(new ClusterSkinBoxSet
            {
                Joints = bounds.OrderBy(pair => pair.Key).Select(pair => new ClusterSkinJointBox(pair.Key,
                    new() { X = pair.Value.Minimum.X, Y = pair.Value.Minimum.Y, Z = pair.Value.Minimum.Z },
                    new() { X = pair.Value.Maximum.X, Y = pair.Value.Maximum.Y, Z = pair.Value.Maximum.Z })).ToImmutableArray(),
            });
        }
        var result = new ClusterSkinBoundsBlock { VertexIdentity = geometry.VertexIdentity, Clusters = clusters };
        try { output.SetBlock(result); }
        catch { result.Dispose(); throw; }
    }
}
