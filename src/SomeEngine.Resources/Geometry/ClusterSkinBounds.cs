using System.Numerics;

namespace SomeEngine.Resources.Geometry;

/// <summary>Mesh-space positions influenced by one source joint.</summary>
public readonly record struct SkinJointBounds(uint Joint, Vector3 Minimum, Vector3 Maximum);

/// <summary>Source simplification group and model-space error for one geometry cluster.</summary>
public readonly record struct SkinClusterLod(int Group, int ParentGroup, int Level, float Error, float PositionError);

/// <summary>Ready joint influence boxes in the geometry block's cluster order.</summary>
public sealed class ClusterSkinBounds
{
    private readonly SkinJointBounds[][] _clusters;
    private readonly SkinClusterLod[] _lod;
    public int ClusterCount => _clusters.Length;
    public ReadOnlySpan<SkinJointBounds> GetCluster(int cluster) => _clusters[cluster];
    public SkinClusterLod GetLod(int cluster) => _lod[cluster];

    public ClusterSkinBounds(IEnumerable<IEnumerable<SkinJointBounds>> clusters, IEnumerable<SkinClusterLod> lod)
    {
        _clusters = clusters.Select(values => values.ToArray()).ToArray();
        _lod = lod.ToArray();
    }
}
