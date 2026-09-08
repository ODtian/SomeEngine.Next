using SomeEngine.Assets.Data;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class PackedClusterTests
{
    [Fact]
    public void LodErrorAndTriangleStartRoundTripIndependently()
    {
        var cluster = new PackedCluster
        {
            LODErrorHalf = 0x3C00,
            TriangleStart = 0x4321,
        };

        Assert.Equal((ushort)0x3C00, cluster.LODErrorHalf);
        Assert.Equal((ushort)0x4321, cluster.TriangleStart);
        Assert.Equal(0x43213C00u, cluster.PackedLodErrorTriangleStart);
    }
}
