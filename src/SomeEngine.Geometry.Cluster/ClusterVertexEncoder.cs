using System.Numerics;

namespace SomeEngine.Geometry.Cluster;

public interface IClusterVertexEncoder<TAttributes> where TAttributes : unmanaged
{
    string Compatibility { get; }

    /// <summary>
    /// The coordinator creates one encoder per batch with the same encoding.
    /// Return an independent encoder, or this for a stateless encoder.
    /// Null serializes the shared Encode call and copies its returned bytes.
    /// </summary>
    IClusterVertexEncoder<TAttributes>? CreateBatchEncoder() => null;

    EncodedClusterVertices Encode(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<TAttributes> attributes,
        ReadOnlySpan<uint> clusterVertices);
}

public interface IClusterVertexEncoder
{
    string Compatibility { get; }

    /// <summary>
    /// The coordinator creates one encoder per batch with the same encoding.
    /// Return an independent encoder, or this for a stateless encoder.
    /// Null serializes the shared Encode call and copies its returned bytes.
    /// </summary>
    IClusterVertexEncoder? CreateBatchEncoder() => null;

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
