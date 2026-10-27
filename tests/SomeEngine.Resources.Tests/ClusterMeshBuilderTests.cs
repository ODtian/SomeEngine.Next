using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Resources.Tests;

public sealed class ClusterMeshBuilderTests
{
    [Fact]
    public void ProceduralGeometryCreatesReadyMeshWithMaterialRegions()
    {
        Vector3[] positions = [new(-1, 0, 0), new(1, 0, 0), new(0, 2, 0)];
        StandardClusterAttributes[] attributes =
        [
            new(Vector3.UnitZ, new(1, 0, 0, 1), new(0, 0)),
            new(Vector3.UnitZ, new(1, 0, 0, 1), new(1, 0)),
            new(Vector3.UnitZ, new(1, 0, 0, 1), new(.5f, 1)),
        ];

        Mesh mesh = ClusterMeshBuilder.Build(positions, attributes, [0u, 1u, 2u],
            [new ClusterMaterialGroup(0, 3, 0)], StandardClusterVertexEncoder.Instance,
            ClusterBuildStrategy.Realtime, "triangle");

        Assert.Equal("triangle", mesh.Name);
        Assert.Equal(StandardClusterVertexEncoder.EncodingCompatibility, mesh.VertexEncoding);
        Assert.Single(mesh.Regions);
        Assert.Equal(new Vec3 { X = 0, Y = 1, Z = 0 }, mesh.Bounds!.Center);
        Assert.True(mesh.TryGetPage(0, out var page));
        Assert.True(page.Length > 0);
        Assert.True(mesh.BvhByteLength > 0);
    }

    [Theory]
    [InlineData(ClusterBuildStrategy.Realtime)]
    [InlineData(ClusterBuildStrategy.Offline)]
    public void TypedBuilderPreservesFullWidthMaterialRegions(ClusterBuildStrategy strategy)
    {
        uint[] materialIds = [0, 1, 255, 256, 1024];
        Vector3[] positions = Enumerable.Range(0, materialIds.Length).SelectMany(index => new Vector3[]
        {
            new(index * 2, 0, 0), new(index * 2 + 1, 0, 0), new(index * 2, 1, 0),
        }).ToArray();
        StandardClusterAttributes[] attributes = Enumerable.Repeat(
            new StandardClusterAttributes(Vector3.UnitZ, new(1, 0, 0, 1), Vector2.Zero),
            positions.Length).ToArray();
        uint[] indices = Enumerable.Range(0, positions.Length).Select(static index => (uint)index).ToArray();
        ClusterMaterialGroup[] groups = materialIds.Select((material, index) =>
            new ClusterMaterialGroup(index * 3, 3, material)).ToArray();

        Mesh mesh = ClusterMeshBuilder.Build(positions, attributes, indices, groups,
            StandardClusterVertexEncoder.Instance, strategy, "full-width material regions");

        Assert.Equal(1025, mesh.Regions.Length);
        ClusterMaterialRange[] ranges = ReadMaterialRanges(mesh).SelectMany(static clusterRanges => clusterRanges).ToArray();
        Assert.Equal(materialIds, ranges.Select(static range => range.MaterialIndex).Distinct().Order().ToArray());
        Assert.All(ranges, static range => Assert.True(range.TriangleCount > 0));
    }

    [Fact]
    public void SharedPageWriterPreservesFiveMaterialRangesInOneCluster()
    {
        uint[] materialIds = [0, 1, 255, 256, 1024];
        // This public input is already one cluster; builder partitioning cannot
        // hide the more-than-three-range path by splitting the source geometry.
        var source = new EncodedClusterGeometry
        {
            VertexBlob = new byte[36],
            TriangleIndices = Enumerable.Range(0, materialIds.Length)
                .SelectMany(static _ => new byte[] { 0, 1, 2 }).ToArray(),
            TriangleMaterials = materialIds,
            VertexCount = 3,
            BoundMin = Vector3.Zero,
            BoundMax = Vector3.One,
            LodSphere = new(.5f, .5f, .5f, 1),
            SelfLodSphere = new(.5f, .5f, .5f, 1),
        };

        Mesh mesh = ClusterMeshPages.Build("five material ranges", "tests.opaque-material-vertices", [source]);

        ClusterMaterialRange[] ranges = Assert.Single(ReadMaterialRanges(mesh));
        Assert.Equal(materialIds, ranges.Select(static range => range.MaterialIndex).Order().ToArray());
        Assert.All(ranges, static range => Assert.Equal(1u, range.TriangleCount));
        Assert.Equal(Enumerable.Range(0, materialIds.Length).Select(static index => (uint)index).ToArray(),
            ranges.Select(static range => range.FirstTriangle).Order().ToArray());
    }

    private static List<ClusterMaterialRange[]> ReadMaterialRanges(Mesh mesh)
    {
        var result = new List<ClusterMaterialRange[]>();
        for (int pageIndex = 0; pageIndex < mesh.PageCount; pageIndex++)
        {
            Assert.True(mesh.TryGetPage(pageIndex, out ReadOnlyMemory<byte> page));
            MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(page.Span);
            ReadOnlySpan<PackedCluster> clusters = MemoryMarshal.Cast<byte, PackedCluster>(page.Span.Slice(
                checked((int)header.ClustersOffset), checked((int)header.ClusterCount * PackedCluster.SizeInBytes)));
            foreach (PackedCluster cluster in clusters)
            {
                var ranges = new ClusterMaterialRange[cluster.MaterialRangeCount];
                for (int range = 0; range < ranges.Length; range++)
                    ranges[range] = cluster.GetMaterialRange(page.Span, range);
                result.Add(ranges);
            }
        }
        return result;
    }
}
