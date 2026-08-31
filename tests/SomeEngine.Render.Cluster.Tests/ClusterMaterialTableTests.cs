using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterMaterialTableTests
{
    [Fact]
    public void Structurally_identical_publication_preserves_snapshot_identity_and_topology_version()
    {
        var table = new ClusterMaterialTable(new ClusterMaterialTypeExecutionResolver());
        var material = new Material { Name = "test" };
        ClusterMaterialSnapshot first = Snapshot(material, shadeBin: 0);
        ClusterMaterialSnapshot identical = Snapshot(material, shadeBin: 0);

        table.Publish(first);
        table.Publish(identical);

        Assert.Same(first, table.Current);
        Assert.Equal(1UL, first.TopologyVersion);
        Assert.Equal(0UL, identical.TopologyVersion);

        ClusterMaterialSnapshot changed = Snapshot(material, shadeBin: 1);
        table.Publish(changed);

        Assert.Same(changed, table.Current);
        Assert.Equal(2UL, changed.TopologyVersion);
    }

    private static ClusterMaterialSnapshot Snapshot(
        Material material,
        uint shadeBin)
    {
        const uint slotCapacity = 2;
        uint[] words =
        [
            0, 0,
            0, 0,
            shadeBin, 0,
        ];
        return new ClusterMaterialSnapshot(
            [new ClusterMaterialSequence([material], 0)],
            [material],
            words,
            slotCapacity,
            rasterBinCount: 1,
            deformBinCount: 1,
            shadeBinCount: shadeBin + 1);
    }
}
