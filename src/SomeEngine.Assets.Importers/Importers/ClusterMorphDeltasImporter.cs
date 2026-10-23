using SharpGLTF.Schema2;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Importers;

[AssetImporter("99da2dee-eeb5-4442-8b0c-501c3fa55650", typeof(ClusterMorphDeltasBlock),
    SettingsProvider = typeof(GltfMeshSettingsProvider), VersionInputs = new[] { typeof(ModelRoot) })]
public sealed class ClusterMorphDeltasImporter : IAssetImporter<GltfMesh, GltfMeshSettings>
{
    public static readonly Guid Id = new("99da2dee-eeb5-4442-8b0c-501c3fa55650");

    public async ValueTask ImportAsync(GltfMesh source, GltfMeshSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        VertexStreamsBlock streams = await context.GetBlockAsync<VertexStreamsBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        if (!streams.Primitives.Any(primitive => primitive.MorphTargets.Count > 0)) return;
        ClusterGeometryBlock? geometry = await context.TryGetBlockAsync<ClusterGeometryBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        if (geometry is null) return;
        Dictionary<int, VertexPrimitive> primitives = streams.Primitives.ToDictionary(primitive => primitive.SourcePrimitive);
        List<ClusterMorphDeltas> clusters = [];
        foreach (ClusterGeometry cluster in geometry.Clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClusterVertexSource[] rows = cluster.Vertices.Select(vertex => geometry.Vertices[(int)vertex]).ToArray();
            int targetCount = rows.Max(row => primitives[row.Primitive].MorphTargets.Count);
            List<ClusterVertexAttributes> targets = [];
            List<ClusterVertexAttribute?> sourceTangents = [];
            for (int target = 0; target < targetCount; target++)
            {
                IEnumerable<string> names = rows.Where(row => target < primitives[row.Primitive].MorphTargets.Count)
                    .SelectMany(row => primitives[row.Primitive].MorphTargets[target].Keys)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                var attributes = names.Select(name => ClusterSourceStreams.Read(name, rows,
                    row => target < primitives[row.Primitive].MorphTargets.Count
                        ? primitives[row.Primitive].MorphTargets[target].GetValueOrDefault(name) : null));
                targets.Add(StandardClusterAttributesEncoder.EncodeSourceValues(attributes));

                VertexStream? OriginalTangents(ClusterVertexSource row)
                {
                    IReadOnlyList<VertexStream?> originals = primitives[row.Primitive].SourceMorphTangents;
                    return target < originals.Count ? originals[target] : null;
                }
                sourceTangents.Add(rows.Any(row => OriginalTangents(row) is not null)
                    ? ClusterSourceStreams.Read("TANGENT", rows, OriginalTangents) : null);
            }
            clusters.Add(new() { Targets = [.. targets], SourceTangents = [.. sourceTangents] });
        }
        var block = new ClusterMorphDeltasBlock { VertexIdentity = geometry.VertexIdentity, Clusters = clusters };
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }
}
