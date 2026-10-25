using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources.Geometry;

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
    public async Task IndependentClusterEncodingPreservesThePageContents()
    {
        var (positions, attributes, indices) = CreateGrid();
        ClusterMaterialGroup[] groups = [new(0, indices.Length, 0)];
        Mesh reference = ClusterMeshBuilder.Build(positions, attributes, indices, groups,
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime, "grid");
        Assert.True(reference.PageCount > 1);
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int entered = 0;
        var encoder = new ObservingEncoder(() =>
        {
            int index = Interlocked.Increment(ref entered);
            if (index > 2) return;
            (index == 1 ? firstEntered : secondEntered).Set();
            release.Wait();
        });
        Task<Mesh> build = Task.Run(() => ClusterMeshBuilder.Build(positions, attributes, indices, groups,
            encoder, ClusterBuildStrategy.Realtime, "grid"));
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.Set();
            await build.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Mesh result = await build;
        Assert.Equal(reference.Bounds, result.Bounds);
        Assert.Equal(reference.PageCount, result.PageCount);
        Assert.Equal(reference.Bvh.ToArray(), result.Bvh.ToArray());
        for (int page = 0; page < reference.PageCount; page++)
        {
            Assert.True(reference.TryGetPage(page, out var expected));
            Assert.True(result.TryGetPage(page, out var actual));
            Assert.Equal(expected.ToArray(), actual.ToArray());
        }
    }

    [Fact]
    public void SharedEncoderCanReuseItsReturnedBytes()
    {
        var (positions, attributes, indices) = CreateGrid();
        Mesh mesh = ClusterMeshBuilder.Build(positions, attributes, indices,
            [new ClusterMaterialGroup(0, indices.Length, 0)], new SharedBoundsEncoder(),
            ClusterBuildStrategy.Realtime, "bounds grid");
        int clusters = 0;
        for (int pageIndex = 0; pageIndex < mesh.PageCount; pageIndex++)
        {
            Assert.True(mesh.TryGetPage(pageIndex, out var page));
            MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(page.Span);
            for (int cluster = 0; cluster < header.ClusterCount; cluster++)
            {
                PackedCluster record = MemoryMarshal.Read<PackedCluster>(page.Span[
                    ((int)header.ClustersOffset + cluster * PackedCluster.SizeInBytes)..]);
                int blobOffset = checked((int)(header.VertexBlobsOffset + record.VertexBlobOffset));
                int boundsOffset = blobOffset + BinaryPrimitives.ReadInt32LittleEndian(page.Span[blobOffset..]);
                ReadOnlySpan<byte> bounds = page.Span[boundsOffset..];
                Assert.Equal(record.BoundMin, new Vector3(BinaryPrimitives.ReadSingleLittleEndian(bounds),
                    BinaryPrimitives.ReadSingleLittleEndian(bounds[4..]), BinaryPrimitives.ReadSingleLittleEndian(bounds[8..])));
                Assert.Equal(record.BoundMax, new Vector3(BinaryPrimitives.ReadSingleLittleEndian(bounds[12..]),
                    BinaryPrimitives.ReadSingleLittleEndian(bounds[16..]), BinaryPrimitives.ReadSingleLittleEndian(bounds[20..])));
                clusters++;
            }
        }
        Assert.True(clusters > 1);
    }

    private static (Vector3[], StandardClusterAttributes[], uint[]) CreateGrid()
    {
        const int triangles = 4096;
        var positions = new Vector3[triangles * 3];
        var attributes = new StandardClusterAttributes[positions.Length];
        var indices = new uint[positions.Length];
        for (int triangle = 0; triangle < triangles; triangle++)
        {
            Vector3 origin = new(triangle % 32 * 3, triangle / 32 * 3, 0);
            positions[triangle * 3] = origin;
            positions[triangle * 3 + 1] = origin + Vector3.UnitX;
            positions[triangle * 3 + 2] = origin + Vector3.UnitY;
        }
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            indices[vertex] = (uint)vertex;
            attributes[vertex] = new(Vector3.UnitZ, new(1, 0, 0, 1), new(vertex % 3, vertex % 2));
        }
        return (positions, attributes, indices);
    }

    private sealed class SharedBoundsEncoder : IClusterVertexEncoder<StandardClusterAttributes>
    {
        private readonly byte[] _bytes = new byte[24];
        public string Compatibility => "test.cluster.bounds.v1";
        public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions,
            ReadOnlySpan<StandardClusterAttributes> attributes, ReadOnlySpan<uint> clusterVertices)
        {
            Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            foreach (uint vertex in clusterVertices)
            {
                min = Vector3.Min(min, positions[(int)vertex]);
                max = Vector3.Max(max, positions[(int)vertex]);
            }
            BinaryPrimitives.WriteSingleLittleEndian(_bytes, min.X);
            BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(4), min.Y);
            BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(8), min.Z);
            BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(12), max.X);
            BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(16), max.Y);
            BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(20), max.Z);
            return new(_bytes, min, max);
        }
    }

    private sealed class ObservingEncoder(Action onEncode) : IClusterVertexEncoder<StandardClusterAttributes>
    {
        public string Compatibility => StandardClusterVertexEncoder.EncodingCompatibility;
        public IClusterVertexEncoder<StandardClusterAttributes> CreateBatchEncoder() => this;
        public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions,
            ReadOnlySpan<StandardClusterAttributes> attributes, ReadOnlySpan<uint> clusterVertices)
        {
            onEncode();
            return StandardClusterVertexEncoder.Instance.Encode(positions, attributes, clusterVertices);
        }
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
