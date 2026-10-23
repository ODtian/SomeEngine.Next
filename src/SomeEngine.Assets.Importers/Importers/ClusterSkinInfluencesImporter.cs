using SharpGLTF.Schema2;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Importers;

[AssetImporter("16ee2d99-92a6-4529-803f-6535b768c327", typeof(ClusterSkinInfluencesBlock),
    SettingsProvider = typeof(GltfMeshSettingsProvider), VersionInputs = new[] { typeof(ModelRoot) })]
public sealed class ClusterSkinInfluencesImporter : IAssetImporter<GltfMesh, GltfMeshSettings>
{
    public static readonly Guid Id = new("16ee2d99-92a6-4529-803f-6535b768c327");

    public async ValueTask ImportAsync(GltfMesh source, GltfMeshSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        VertexStreamsBlock streams = await context.GetBlockAsync<VertexStreamsBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        if (!streams.Primitives.Any(primitive => primitive.Attributes.Keys.Any(IsInfluence))) return;
        ClusterGeometryBlock? geometry = await context.TryGetBlockAsync<ClusterGeometryBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        if (geometry is null) return;
        Dictionary<int, VertexPrimitive> primitives = streams.Primitives.ToDictionary(primitive => primitive.SourcePrimitive);
        List<ClusterVertexAttributes> clusters = [];
        foreach (ClusterGeometry cluster in geometry.Clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClusterVertexSource[] rows = cluster.Vertices.Select(vertex => geometry.Vertices[(int)vertex]).ToArray();
            IEnumerable<string> names = rows.SelectMany(row => primitives[row.Primitive].Attributes.Keys)
                .Where(IsInfluence).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var attributes = names.Select(name => ClusterSourceStreams.Read(name, rows,
                row => primitives[row.Primitive].Attributes.GetValueOrDefault(name)));
            clusters.Add(StandardClusterAttributesEncoder.EncodeSourceValues(attributes));
        }
        var block = new ClusterSkinInfluencesBlock { VertexIdentity = geometry.VertexIdentity, Clusters = clusters };
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }

    private static bool IsInfluence(string name)
        => name.StartsWith("JOINTS_", StringComparison.Ordinal) || name.StartsWith("WEIGHTS_", StringComparison.Ordinal);
}
