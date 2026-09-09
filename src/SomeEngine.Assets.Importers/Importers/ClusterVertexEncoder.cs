using System.Numerics;

namespace SomeEngine.Assets.Importers;

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
    Vector3 BoundMax);
