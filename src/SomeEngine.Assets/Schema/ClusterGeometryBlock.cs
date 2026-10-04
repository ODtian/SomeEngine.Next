using System.Buffers.Binary;
using System.Numerics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[Flags]
public enum ClusterDeform
{
    Static = 0,
    Skin = 1,
    Morph = 2,
}

/// <summary>The expanded stream row and original accessor row represented by a welded vertex.</summary>
[BinarySerializable, BinaryValue]
[BinaryFields(nameof(Primitive), nameof(Vertex), nameof(SourceVertex))]
public readonly partial record struct ClusterVertexSource(int Primitive, int Vertex, uint SourceVertex);

/// <summary>One cluster's quantized positions, local triangles and LOD group correspondence.</summary>
[BinarySerializable]
public sealed partial record ClusterGeometry
{
    public ClusterDeform Deform { get; init; }
    public ImmutableArray<uint> Vertices { get; init; } = [];
    public ImmutableArray<byte> Indices { get; init; } = [];
    public ImmutableArray<int> TriangleMaterials { get; init; } = [];
    public SomeEngine.Resources.Vec3 PositionOrigin { get; init; } = new();
    public float PositionStep { get; init; }
    public ImmutableArray<byte> PositionData { get; init; } = [];
    public int Level { get; init; }
    public float Error { get; init; }
    public float ParentError { get; init; }
    public int Group { get; init; }
    public int ParentGroup { get; init; }
    public SomeEngine.Resources.Vec3 Center { get; init; } = new();
    public float Radius { get; init; }
    public SomeEngine.Resources.Vec3 LodCenter { get; init; } = new();
    public float LodRadius { get; init; }

    public Vector3 DecodePosition(int vertex)
    {
        ReadOnlySpan<byte> data = PositionData.AsSpan().Slice(checked(vertex * 6), 6);
        return new Vector3(PositionOrigin.X, PositionOrigin.Y, PositionOrigin.Z) + new Vector3(BinaryPrimitives.ReadUInt16LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data[2..]), BinaryPrimitives.ReadUInt16LittleEndian(data[4..])) * PositionStep;
    }
}

/// <summary>Cluster vertex identity, geometry and LOD, shared by the pipeline's per-vertex blocks.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ClusterGeometryBlock"), BinarySerializable]
[BinaryFields(nameof(VertexIdentity), nameof(PositionEncoding), nameof(Vertices), nameof(Clusters))]
public sealed partial class ClusterGeometryBlock : AssetBlock
{
    public const string EncodingCompatibility = "someengine.cluster.positions.uint16.v1";
    public partial Guid VertexIdentity { get; set; }
    public partial string PositionEncoding { get; set; }
    public partial IList<ClusterVertexSource> Vertices { get; set; }
    public partial IList<ClusterGeometry> Clusters { get; set; }

    partial void Initialize()
    {
        VertexIdentity = Guid.NewGuid();
        PositionEncoding = EncodingCompatibility;
    }
}
