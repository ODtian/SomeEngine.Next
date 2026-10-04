using System.Buffers.Binary;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Importers;

[AssetImporter("535b2c57-2964-483b-867a-f38ef1a9fe5b", typeof(ClusterVertexAttributesBlock),
    SettingsProvider = typeof(GltfMeshSettingsProvider), VersionInputs = new[] { typeof(ModelRoot) })]
public sealed class ClusterVertexAttributesImporter : IAssetImporter<GltfMesh, GltfMeshSettings>
{
    public async ValueTask ImportAsync(GltfMesh source, GltfMeshSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        VertexStreamsBlock streams = await context.GetBlockAsync<VertexStreamsBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        ClusterGeometryBlock geometry = await context.GetBlockAsync<ClusterGeometryBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        Dictionary<int, VertexPrimitive> primitives = streams.Primitives.ToDictionary(primitive => primitive.SourcePrimitive);
        List<ClusterVertexAttributes> clusters = [];
        foreach (ClusterGeometry cluster in geometry.Clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = cluster.Vertices.Select(vertex => geometry.Vertices[(int)vertex]).ToArray();
            // Each row retains its source component count, including absence, when schemas differ.
            var names = rows.SelectMany(row => primitives[row.Primitive].Attributes.Keys)
                .Where(IsShadingAttribute).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            List<ClusterVertexAttribute> attributes = [];
            foreach (string name in names)
            {
                int components = rows.Where(row => primitives[row.Primitive].Attributes.ContainsKey(name))
                    .Max(row => primitives[row.Primitive].Attributes[name].ComponentCount);
                byte[] data = new byte[rows.Length * components * sizeof(double)];
                int[] componentCounts = new int[rows.Length];
                for (int vertex = 0; vertex < rows.Length; vertex++)
                {
                    ClusterVertexSource row = rows[vertex];
                    if (!primitives[row.Primitive].Attributes.TryGetValue(name, out VertexStream? stream)) continue;
                    componentCounts[vertex] = stream.ComponentCount;
                    for (int component = 0; component < stream.ComponentCount; component++)
                        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan((vertex * components + component) * sizeof(double)),
                            stream.Values.Span[row.Vertex * stream.ComponentCount + component]);
                }
                attributes.Add(new() { Name = name, ComponentCount = components, VertexComponentCounts = [.. componentCounts], Data = [.. data] });
            }
            clusters.Add(new() { Attributes = [.. attributes] });
        }
        var block = new ClusterVertexAttributesBlock { VertexIdentity = geometry.VertexIdentity, Clusters = clusters };
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }

    private static bool IsShadingAttribute(string name)
        => name != "POSITION" && !name.StartsWith("JOINTS_", StringComparison.Ordinal) && !name.StartsWith("WEIGHTS_", StringComparison.Ordinal);
}
