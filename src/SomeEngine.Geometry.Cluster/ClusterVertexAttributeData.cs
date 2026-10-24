using System.Buffers.Binary;
using System.Collections.Immutable;

namespace SomeEngine.Geometry.Cluster;

public readonly record struct ClusterVertexAttributeData(string Name, int ComponentCount,
    ImmutableArray<int> VertexComponentCounts, ImmutableArray<byte> Data)
{
    public int VertexCount => Data.Length / (ComponentCount * sizeof(double));
    public double ReadComponent(int vertex, int component)
        => BinaryPrimitives.ReadDoubleLittleEndian(Data.AsSpan().Slice((vertex * ComponentCount + component) * sizeof(double)));
}
