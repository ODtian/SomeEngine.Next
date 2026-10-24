using SomeEngine.Assets.Schema;
using SomeEngine.Geometry.Cluster;

namespace SomeEngine.Assets.Importers;

internal static class StandardClusterAttributesEncoder
{
    internal static ClusterVertexAttributes Encode(IEnumerable<ClusterVertexAttribute> attributes)
        => Encode(attributes, false);

    internal static ClusterVertexAttributes EncodeSourceValues(IEnumerable<ClusterVertexAttribute> attributes)
        => Encode(attributes, true);

    private static ClusterVertexAttributes Encode(IEnumerable<ClusterVertexAttribute> attributes, bool sourceValues)
    {
        ClusterVertexAttribute[] streams = attributes.ToArray();
        var data = streams.Select(stream => new ClusterVertexAttributeData(stream.Name, stream.ComponentCount,
            stream.VertexComponentCounts, stream.Data));
        byte[] bytes = sourceValues
            ? SomeEngine.Geometry.Cluster.StandardClusterAttributesEncoder.EncodeSourceValues(data)
            : SomeEngine.Geometry.Cluster.StandardClusterAttributesEncoder.Encode(data);
        return new() { Data = [.. bytes], Attributes = [.. streams] };
    }
}
