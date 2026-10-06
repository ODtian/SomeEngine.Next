using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources.Geometry;
using GeometryValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterTriangleEdgeTests
{
    [Fact]
    public async Task ThreeFourFiveTriangleCarriesItsLongestSourceEdge()
    {
        Vector3[] positions = [Vector3.Zero, new(3, 0, 0), new(0, 4, 0)];
        List<RawAttribute> attributes =
        [
            new("NORMAL", new float[] { 0,0,1, 0,0,1, 0,0,1 }, 3, GeometryValueType.Int8, 3, true),
            new("TEXCOORD_0", new float[6], 2, GeometryValueType.Float16, 2, false),
        ];
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions, attributes, [0, 1, 2],
            [new MeshMaterialSlot(Guid.Empty)], "Three four five triangle");
        ClusterMeshBlock geometry = asset.GetBlock<ClusterMeshBlock>();
        var mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        await geometry.LoadPagesAsync(mesh, [0]);
        Assert.True(mesh.TryGetPage(0, out var bytes));
        MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(bytes.Span);
        PackedCluster cluster = MemoryMarshal.Read<PackedCluster>(bytes.Span[(int)header.ClustersOffset..]);
        Assert.Equal(5f, (float)BitConverter.UInt16BitsToHalf(cluster.MaximumEdgeLengthHalf));
    }
}
