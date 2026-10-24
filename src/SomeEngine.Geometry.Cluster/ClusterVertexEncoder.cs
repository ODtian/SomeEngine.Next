using System.Numerics;

namespace SomeEngine.Geometry.Cluster;

public interface IClusterVertexEncoder<TAttributes> where TAttributes : unmanaged
{
    string Compatibility { get; }

    EncodedClusterVertices Encode(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<TAttributes> attributes,
        ReadOnlySpan<uint> clusterVertices);
}

public interface IClusterVertexEncoder
{
    string Compatibility { get; }

    EncodedClusterVertices Encode(
        ReadOnlySpan<Vector3> positions,
        IReadOnlyList<RawAttribute> attributes,
        ReadOnlySpan<uint> clusterVertices);
}

public readonly record struct EncodedClusterVertices(
    ReadOnlyMemory<byte> Blob,
    Vector3 BoundMin,
    Vector3 BoundMax,
    int PositionBytes = 0);
