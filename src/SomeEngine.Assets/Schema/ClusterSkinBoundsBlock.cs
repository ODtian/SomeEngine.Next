using System.Numerics;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[BinarySerializable, BinaryValue]
public readonly partial record struct ClusterSkinJointBox(uint Joint, Vec3 Minimum, Vec3 Maximum);

[BinarySerializable]
public sealed partial record ClusterSkinBoxSet
{
    public ImmutableArray<ClusterSkinJointBox> Joints { get; init; } = [];
}

/// <summary>LBS influence boxes following the shared geometry vertex identity.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ClusterSkinBoundsBlock"), BinarySerializable]
[BinaryFields(nameof(VertexIdentity), nameof(Clusters))]
public sealed partial class ClusterSkinBoundsBlock : AssetBlock
{
    public partial Guid VertexIdentity { get; set; }
    public partial IList<ClusterSkinBoxSet> Clusters { get; set; }

    public ClusterSkinBounds CreateBounds() => new(Clusters.Select(cluster => cluster.Joints.Select(joint =>
        new SkinJointBounds(joint.Joint,
            new Vector3(joint.Minimum.X, joint.Minimum.Y, joint.Minimum.Z),
            new Vector3(joint.Maximum.X, joint.Maximum.Y, joint.Maximum.Z)))));
}
