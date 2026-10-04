using System.Buffers.Binary;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>A cluster-local stream encoded as little-endian IEEE 754 doubles in vertex/component order.</summary>
[BinarySerializable]
public sealed partial record ClusterVertexAttribute
{
    public string Name { get; init; } = "";
    public int ComponentCount { get; init; }
    public ImmutableArray<int> VertexComponentCounts { get; init; } = [];
    public ImmutableArray<byte> Data { get; init; } = [];
    [BinaryIgnore] public int VertexCount => Data.Length / (ComponentCount * sizeof(double));

    public double ReadComponent(int vertex, int component)
        => BinaryPrimitives.ReadDoubleLittleEndian(Data.AsSpan().Slice(checked((vertex * ComponentCount + component) * sizeof(double)), sizeof(double)));
}

[BinarySerializable]
public sealed partial record ClusterVertexAttributes
{
    public ImmutableArray<ClusterVertexAttribute> Attributes { get; init; } = [];
}

/// <summary>All shading attributes in the geometry block's cluster and vertex order.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ClusterVertexAttributesBlock"), BinarySerializable]
[BinaryFields(nameof(VertexIdentity), nameof(Encoding), nameof(Clusters))]
public sealed partial class ClusterVertexAttributesBlock : AssetBlock
{
    public const string EncodingCompatibility = "someengine.cluster.attributes.float64.v1";
    public partial Guid VertexIdentity { get; set; }
    public partial string Encoding { get; set; }
    public partial IList<ClusterVertexAttributes> Clusters { get; set; }

    partial void Initialize() => Encoding = EncodingCompatibility;
}
