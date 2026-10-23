using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Morph target streams and retained source tangent deltas for one cluster.</summary>
[BinarySerializable]
public sealed partial record ClusterMorphDeltas
{
    public ImmutableArray<ClusterVertexAttributes> Targets { get; init; } = [];
    public ImmutableArray<ClusterVertexAttribute?> SourceTangents { get; init; } = [];
}

/// <summary>All morph targets in source target order and the geometry block's cluster and vertex order.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ClusterMorphDeltasBlock"), BinarySerializable]
[BinaryFields(nameof(VertexIdentity), nameof(Encoding), nameof(Clusters))]
public sealed partial class ClusterMorphDeltasBlock : AssetBlock
{
    public const string EncodingCompatibility = "someengine.cluster.morph.float64.v1";
    public partial Guid VertexIdentity { get; set; }
    public partial string Encoding { get; set; }
    public partial IList<ClusterMorphDeltas> Clusters { get; set; }

    partial void Initialize() => Encoding = EncodingCompatibility;
}
