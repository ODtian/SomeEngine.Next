using System.Buffers.Binary;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

internal static class ClusterVertexAttributesEncoder
{
    internal static ClusterVertexAttributesBlock BuildAttributes(VertexStreamsBlock streams, ClusterGeometryBlock geometry,
        CancellationToken cancellationToken = default)
    {
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
            clusters.Add(StandardClusterAttributesEncoder.Encode(attributes));
        }
        return new ClusterVertexAttributesBlock { VertexIdentity = geometry.VertexIdentity, Clusters = clusters };
    }

    private static bool IsShadingAttribute(string name)
        => name != "POSITION" && !name.StartsWith("JOINTS_", StringComparison.Ordinal) && !name.StartsWith("WEIGHTS_", StringComparison.Ordinal);
}
