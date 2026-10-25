using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Resources.Geometry;
using SomeEngine.Geometry.Cluster;

namespace SomeEngine.Resources.Tests;

public sealed class ClusterMeshBuilderTests
{
    [Theory]
    [InlineData(ClusterBuildStrategy.Realtime)]
    [InlineData(ClusterBuildStrategy.Offline)]
    public void BvhBoundsCoverDistributedGeometry(ClusterBuildStrategy strategy)
    {
        const int triangleCount = 4096;
        Vector3[] positions = new Vector3[triangleCount * 3];
        StandardClusterAttributes[] attributes = new StandardClusterAttributes[positions.Length];
        uint[] indices = new uint[positions.Length];
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            int vertex = triangle * 3;
            Vector3 origin = new((triangle % 32) * 4, (triangle / 32) * 4, 0);
            positions[vertex] = origin;
            positions[vertex + 1] = origin + Vector3.UnitX;
            positions[vertex + 2] = origin + Vector3.UnitY;
        }
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            attributes[vertex] = new(Vector3.UnitZ, new(1, 0, 0, 1), Vector2.Zero);
            indices[vertex] = (uint)vertex;
        }
        Mesh mesh = ClusterMeshBuilder.Build(positions, attributes, indices,
            [new ClusterMaterialGroup(0, indices.Length, 0)], StandardClusterVertexEncoder.Instance,
            strategy, "distributed triangles");
        ReadOnlySpan<ClusterBVHNode> nodes = MemoryMarshal.Cast<byte, ClusterBVHNode>(mesh.Bvh.Span);
        ClusterBVHNode root = nodes[^1];
        Assert.Equal(0u, root.NodeType);
        foreach (Vector3 position in positions)
            AssertContains(root, position);
        foreach (ClusterBVHNode parent in nodes)
        {
            if (parent.NodeType == 1) continue;
            for (int child = 0; child < parent.ChildCount; child++)
            {
                ClusterBVHNode node = nodes[(int)parent.ChildPointer + child];
                AssertContains(parent, new(node.BoundMin.X, node.BoundMin.Y, node.BoundMin.Z));
                AssertContains(parent, new(node.BoundMax.X, node.BoundMax.Y, node.BoundMax.Z));
            }
        }
    }

    private static void AssertContains(ClusterBVHNode node, Vector3 position)
    {
        Assert.InRange(position.X, node.BoundMin.X, node.BoundMax.X);
        Assert.InRange(position.Y, node.BoundMin.Y, node.BoundMax.Y);
        Assert.InRange(position.Z, node.BoundMin.Z, node.BoundMax.Z);
    }

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
