using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>All source joint and weight streams in the geometry block's cluster and vertex order.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ClusterSkinInfluencesBlock"), BinarySerializable]
[BinaryFields(nameof(VertexIdentity), nameof(Encoding), nameof(Clusters))]
public sealed partial class ClusterSkinInfluencesBlock : AssetBlock
{
    public const string EncodingCompatibility = "someengine.cluster.skin.float64.v1";
    public partial Guid VertexIdentity { get; set; }
    public partial string Encoding { get; set; }
    public partial IList<ClusterVertexAttributes> Clusters { get; set; }

    partial void Initialize() => Encoding = EncodingCompatibility;
}
