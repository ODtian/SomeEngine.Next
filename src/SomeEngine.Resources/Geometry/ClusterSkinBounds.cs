using System.Numerics;

namespace SomeEngine.Resources.Geometry;

/// <summary>Mesh-space positions influenced by one source joint.</summary>
public readonly record struct SkinJointBounds(uint Joint, Vector3 Minimum, Vector3 Maximum);

/// <summary>Ready joint influence boxes in the geometry block's cluster order.</summary>
public sealed class ClusterSkinBounds
{
    private readonly SkinJointBounds[][] _clusters;
    public int ClusterCount => _clusters.Length;
    public ReadOnlySpan<SkinJointBounds> GetCluster(int cluster) => _clusters[cluster];

    public ClusterSkinBounds(IEnumerable<IEnumerable<SkinJointBounds>> clusters) =>
        _clusters = clusters.Select(values => values.ToArray()).ToArray();
}
