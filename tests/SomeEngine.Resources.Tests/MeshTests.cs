using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Resources.Tests;

public sealed class MeshTests
{
    private const string Encoding = "tests.opaque-vertex-12";

    [Fact]
    public void ReadyGeometryAndBvhViewsSurviveReplacement()
    {
        byte[] payload = Payload(out int offset);
        Mesh mesh = Mesh.Create("ready", Encoding, payload, (ulong)offset);
        Assert.Equal(1, mesh.PageCount);
        Assert.True(mesh.TryGetPage(0, out ReadOnlyMemory<byte> page));
        ReadOnlyMemory<byte> bvh = mesh.Bvh;
        Mesh copy = mesh.Duplicate();
        Assert.True(copy.TryGetPage(0, out var shared));
        Assert.True(page.Equals(shared));
        mesh.Update(Mesh.Create("replacement", Encoding, payload, (ulong)offset));
        Assert.Equal(payload[..offset], page.ToArray());
        Assert.Equal(payload[offset..], bvh.ToArray());
    }

    [Fact]
    public void MissingPagesRequireExplicitSupplyAndBadPagesDoNotChangeTheMesh()
    {
        byte[] payload = Payload(out int offset);
        var descriptions = MeshIntegrity.Inspect(payload, offset, Encoding);
        Mesh mesh = Mesh.Create("partial", Encoding, descriptions, payload.AsMemory(offset));
        Assert.False(mesh.TryGetPage(0, out _));
        Assert.Throws<InvalidOperationException>(() => mesh.ReadPage(0, new byte[offset]));
        byte[] corrupt = payload[..offset];
        corrupt[^1] = 99;
        Assert.Throws<InvalidDataException>(() => mesh.AddPages([(0, corrupt)], mesh.Revision));
        Assert.False(mesh.TryGetPage(0, out _));
        Mesh captured = mesh.CaptureData();
        ulong version = mesh.Revision;
        mesh.AddPages([(0, payload.AsMemory(0, offset))], mesh.Revision);
        Assert.Equal(version + 1, mesh.Revision);
        Assert.False(captured.TryGetPage(0, out _));
        Assert.True(mesh.CaptureData().TryGetPage(0, out _));
        byte[] restored = new byte[offset];
        mesh.ReadPage(0, restored);
        Assert.Equal(payload[..offset], restored);
        Mesh replacement = Mesh.Create("replacement", Encoding, payload, (ulong)offset);
        ulong previousRevision = mesh.Revision;
        mesh.Update(replacement);
        Assert.Equal("replacement", mesh.Name);
        Assert.Equal(previousRevision + 1, mesh.Revision);
        Assert.Throws<InvalidOperationException>(() => mesh.AddPages([(0, restored)], previousRevision));
    }

    [Fact]
    public void CreationRejectsWrongVertexEncodingAndInvalidBvhDivision()
    {
        byte[] payload = Payload(out int offset);
        Assert.Throws<InvalidDataException>(() => Mesh.Create("wrong", "another-encoding", payload.AsMemory(), (ulong)offset));
        Assert.Throws<InvalidDataException>(() => Mesh.Create("wrong", Encoding, payload.AsMemory(), (ulong)offset + 1));
    }

    private static byte[] Payload(out int bvhOffset)
    {
        int vertices = MeshPageHeader.Size + PackedCluster.SizeInBytes;
        bvhOffset = vertices + 12 + 3;
        byte[] bytes = new byte[bvhOffset + Unsafe.SizeOf<ClusterBVHNode>()];
        var header = new MeshPageHeader
        {
            ClusterCount = 1, TotalVertexCount = 3, TotalTriangleCount = 1, VertexBlobBytes = 12,
            ClustersOffset = MeshPageHeader.Size, VertexBlobsOffset = (uint)vertices,
            IndicesOffset = (uint)(vertices + 12), ExtensionDataOffset = (uint)bvhOffset,
            VertexEncodingHash = VertexEncodingCompatibility.Hash(Encoding),
        };
        MemoryMarshal.Write(bytes, in header);
        var cluster = new PackedCluster
        {
            VertexBlobSize = 12, ExtensionOffset = uint.MaxValue, MaterialTableOffset = uint.MaxValue,
            PackedCounts = 3 | (1u << 8),
        };
        MemoryMarshal.Write(bytes.AsSpan(MeshPageHeader.Size), in cluster);
        bytes[bvhOffset - 3] = 0; bytes[bvhOffset - 2] = 1; bytes[bvhOffset - 1] = 2;
        var node = new ClusterBVHNode();
        var child = new ClusterHierarchySlice { PageRangeKey = 1, GroupPartData = 1u << 16 };
        child.SetErrors(0, float.MaxValue);
        node.SetChild(0, child);
        MemoryMarshal.Write(bytes.AsSpan(bvhOffset), in node);
        return bytes;
    }
}
