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
        int count = clusterVertices.Length;
        var localPositions = new Vector3[count];
        var localVertices = new uint[count];
        var normals = new float[count * 3];
        var tangents = new float[count * 4];
        var texCoords = new float[count * 2];
        for (int index = 0; index < count; index++)
        {
            int source = (int)clusterVertices[index];
            localPositions[index] = positions[source];
            localVertices[index] = (uint)index;
            StandardClusterAttributes vertex = attributes[source];
            normals[index * 3] = vertex.Normal.X;
            normals[index * 3 + 1] = vertex.Normal.Y;
            normals[index * 3 + 2] = vertex.Normal.Z;
            tangents[index * 4] = vertex.Tangent.X;
            tangents[index * 4 + 1] = vertex.Tangent.Y;
            tangents[index * 4 + 2] = vertex.Tangent.Z;
            tangents[index * 4 + 3] = vertex.Tangent.W;
            texCoords[index * 2] = vertex.TexCoord.X;
            texCoords[index * 2 + 1] = vertex.TexCoord.Y;
        }
        return Encode(localPositions, new RawAttribute[]
        {
            new("NORMAL", normals, 3, Resources.Geometry.ValueType.Float32, 3, false),
            new("TANGENT", tangents, 4, Resources.Geometry.ValueType.Float32, 4, false),
            new("TEXCOORD_0", texCoords, 2, Resources.Geometry.ValueType.Float32, 2, false),
        }, localVertices);
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
