using System.Numerics;
using SomeEngine.Geometry.Cluster;

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
}
