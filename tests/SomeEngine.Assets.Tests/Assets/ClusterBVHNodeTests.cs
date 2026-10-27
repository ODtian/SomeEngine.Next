using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Assets.Tests.Assets;

public class ClusterBVHNodeTests
{
    [Fact]
    public void FourChildSlicesRoundTripInThe240BytePackedLayout()
    {
        var node = new ClusterBVHNode();
        var expected = new ClusterHierarchySlice[ClusterBVHNode.ChildCount];
        Assert.Equal(4, ClusterBVHNode.ChildCount);
        Assert.Equal(240, ClusterBVHNode.SizeInBytes);
        for (int child = 0; child < expected.Length; child++)
        {
            expected[child] = new()
            {
                LODBounds = new(child + 1, child + 2, child + 3, child + 4),
                BoundsCenter = new(child + 5, child + 6, child + 7),
                BoundsExtent = new(child + 8, child + 9, child + 10),
                PackedErrors = 0x3C004000u + (uint)child,
                ChildReference = 0x40000000u | (uint)(31 + child),
                PageRangeKey = 0x40000000u | ((uint)(1024 + child) << 5) | (uint)(child + 1),
                GroupPartData = ((uint)(24 + child) << 16) | (uint)(0x8000 + child),
                BoneData = uint.MaxValue - (uint)child,
            };
            node.SetChild(child, expected[child]);
        }
        ReadOnlySpan<ClusterBVHNode> nodes = [node];
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(nodes);
        Assert.Equal(240, bytes.Length);
        for (int child = 0; child < expected.Length; child++)
        {
            ClusterHierarchySlice actual = node.GetChild(child);
            Assert.Equal(expected[child].LODBounds, actual.LODBounds);
            Assert.Equal(expected[child].BoundsCenter, actual.BoundsCenter);
            Assert.Equal(expected[child].BoundsExtent, actual.BoundsExtent);
            Assert.Equal(expected[child].PackedErrors, actual.PackedErrors);
            Assert.Equal(expected[child].ChildReference, actual.ChildReference);
            Assert.Equal(expected[child].PageRangeKey, actual.PageRangeKey);
            Assert.Equal(expected[child].GroupPartData, actual.GroupPartData);
            Assert.Equal(expected[child].BoneData, actual.BoneData);
            Assert.Equal(expected[child].LODBounds.X, BinaryPrimitives.ReadSingleLittleEndian(bytes[(child * 16)..]));
            Assert.Equal(expected[child].PackedErrors, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(64 + child * 16 + 12)..]));
            Assert.Equal(expected[child].ChildReference, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(128 + child * 16 + 12)..]));
            Assert.Equal(expected[child].PageRangeKey, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(192 + child * 12)..]));
            Assert.Equal(expected[child].GroupPartData, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(196 + child * 12)..]));
            Assert.Equal(expected[child].BoneData, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(200 + child * 12)..]));
        }
    }

    [Fact]
    public void LeafFieldBoundariesPreserveTheOtherPackedBits()
    {
        var node = new ClusterBVHNode();
        var expected = new ClusterHierarchySlice
        {
            ChildReference = 0xFFFFFEFFu,
            GroupPartData = uint.MaxValue,
            PageRangeKey = 0x40000000u | (0x1FFFFFFu << 5) | 31u,
        };
        node.SetChild(3, expected);
        ClusterHierarchySlice actual = node.GetChild(3);
        Assert.Equal(255u, actual.FirstCluster);
        Assert.Equal(65535u, actual.ClusterCount);
        Assert.Equal(0x1FFFFFFu, actual.FirstPage);
        Assert.Equal(31u, actual.PageCount);
        Assert.Equal(expected.ChildReference, actual.ChildReference);
        Assert.Equal(expected.GroupPartData, actual.GroupPartData);
        Assert.Equal(expected.PageRangeKey, actual.PageRangeKey);
        Assert.True(actual.IsEnabled);
        Assert.True(actual.IsLeaf);
    }

    [Theory]
    [InlineData(0u, 0u, false, true)]
    [InlineData(0u, 65536u, true, true)]
    [InlineData(1u, 0u, true, true)]
    [InlineData(uint.MaxValue, 0u, true, false)]
    public void DisabledLeafAndInternalSliceStatesUseTheCurrentSentinels(
        uint pageRangeKey, uint groupPartData, bool enabled, bool leaf)
    {
        var node = new ClusterBVHNode();
        var expected = new ClusterHierarchySlice
        {
            PageRangeKey = pageRangeKey,
            GroupPartData = groupPartData,
            ChildReference = 0x12345678u,
        };
        node.SetChild(0, expected);
        ClusterHierarchySlice actual = node.GetChild(0);
        Assert.Equal(enabled, actual.IsEnabled);
        Assert.Equal(leaf, actual.IsLeaf);
        Assert.Equal(expected.ChildReference, actual.ChildReference);
    }

    [Fact]
    public void HierarchyProducerAcceptsTheMaximumPackedPageRangeAndRejectsLargerRanges()
    {
        // Fill each page with one opaque vertex blob so this exercises the shared
        // public page producer, not a disconnected hierarchy-only helper.
        static byte[] PageVertices(int capacity) =>
            new byte[(capacity - MeshPageHeader.Size - PackedCluster.SizeInBytes - 3) & ~3];

        byte[] rootVertices = PageVertices(MeshPageHeader.RootPageSize);
        byte[] streamingVertices = PageVertices(MeshPageHeader.MaxPageSize);
        EncodedClusterGeometry[] clusters = Enumerable.Range(0, 32).Select(index =>
        {
            Vector3 center = new(index, 0, 0);
            return new EncodedClusterGeometry
            {
                VertexBlob = index == 0 ? rootVertices : streamingVertices,
                TriangleIndices = new byte[] { 0, 1, 2 },
                TriangleMaterials = new uint[] { 0 },
                VertexCount = 3,
                BoundMin = center,
                BoundMax = center + Vector3.One,
                Center = center,
                LodSphere = new(15.5f, .5f, .5f, 32),
                SelfLodSphere = new(center + new Vector3(.5f), 1),
                ParentError = 1,
                ParentGroup = 7,
                SourceClusterIndex = (uint)index,
            };
        }).ToArray();

        Mesh boundary = ClusterMeshPages.Build("page-range-boundary", "tests.opaque-vertices", clusters[..31]);
        Assert.Equal(31, boundary.PageCount);
        int leafCount = 0;
        foreach (ClusterBVHNode node in MemoryMarshal.Cast<byte, ClusterBVHNode>(boundary.Bvh.Span))
            for (int child = 0; child < ClusterBVHNode.ChildCount; child++)
            {
                ClusterHierarchySlice leaf = node.GetChild(child);
                if (!leaf.IsEnabled || !leaf.IsLeaf) continue;
                Assert.Equal(0u, leaf.FirstPage);
                Assert.Equal(31u, leaf.PageCount);
                leafCount++;
            }
        Assert.Equal(31, leafCount);
        Assert.Throws<InvalidDataException>(() =>
            ClusterMeshPages.Build("page-range-overflow", "tests.opaque-vertices", clusters));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void ChildAccessRejectsIndicesOutsideTheFourStoredSlices(int child)
    {
        var node = new ClusterBVHNode();
        Assert.Throws<IndexOutOfRangeException>(() => node.SetChild(child, default));
        Assert.Throws<IndexOutOfRangeException>(() => { _ = node.GetChild(child); });
    }
}
