using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;

namespace SomeEngine.Geometry.Cluster;

public readonly record struct StandardClusterAttributes(Vector3 Normal, Vector4 Tangent, Vector2 TexCoord);

/// <summary>Encodes the standard material's separate position and shading data.</summary>
public sealed class StandardClusterVertexEncoder : IClusterVertexEncoder, IClusterVertexEncoder<StandardClusterAttributes>
{
    public const string EncodingCompatibility = "someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1";
    public static StandardClusterVertexEncoder Instance { get; } = new();
    private StandardClusterVertexEncoder() { }
    public string Compatibility => EncodingCompatibility;

    public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<StandardClusterAttributes> attributes, ReadOnlySpan<uint> clusterVertices)
    {
        // Position encoding checks every selected index before quantization. Only a
        // shorter attribute span needs a separate check to retain failure precedence.
        if (attributes.Length < positions.Length)
            foreach (uint vertex in clusterVertices)
                _ = attributes[(int)vertex];
        EncodedClusterPositions encodedPositions = StandardClusterPositionEncoder.Encode(positions, clusterVertices);
        byte[] blob = new byte[encodedPositions.Data.Length
            + StandardClusterAttributesEncoder.GetStandardByteCount(clusterVertices.Length)];
        encodedPositions.Data.AsSpan().CopyTo(blob);
        StandardClusterAttributesEncoder.EncodeStandard(attributes, clusterVertices,
            blob.AsSpan(encodedPositions.Data.Length));
        return new(blob, encodedPositions.BoundMin, encodedPositions.BoundMax, encodedPositions.Data.Length);
    }

    public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions,
        IReadOnlyList<RawAttribute> attributes, ReadOnlySpan<uint> clusterVertices)
    {
        EncodedClusterPositions encodedPositions = StandardClusterPositionEncoder.Encode(positions, clusterVertices);
        var streams = new List<ClusterVertexAttributeData>();
        foreach (RawAttribute attribute in attributes)
        {
            if (attribute.Name is "POSITION" or "_MATERIAL_INDEX") continue;
            byte[] values = new byte[clusterVertices.Length * attribute.Dimension * sizeof(double)];
            for (int vertex = 0; vertex < clusterVertices.Length; vertex++)
                for (int component = 0; component < attribute.Dimension; component++)
                    BinaryPrimitives.WriteDoubleLittleEndian(values.AsSpan((vertex * attribute.Dimension + component) * sizeof(double)),
                        attribute.Data[(int)clusterVertices[vertex] * attribute.Dimension + component]);
            streams.Add(new(attribute.Name, attribute.Dimension,
                [.. Enumerable.Repeat(attribute.Dimension, clusterVertices.Length)], [.. values]));
        }
        byte[] encodedAttributes = StandardClusterAttributesEncoder.Encode(streams);
        byte[] blob = new byte[encodedPositions.Data.Length + encodedAttributes.Length];
        encodedPositions.Data.AsSpan().CopyTo(blob);
        encodedAttributes.CopyTo(blob, encodedPositions.Data.Length);
        return new(blob, encodedPositions.BoundMin, encodedPositions.BoundMax, encodedPositions.Data.Length);
    }
}
