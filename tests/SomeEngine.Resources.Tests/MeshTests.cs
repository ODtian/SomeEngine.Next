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
    public void ReadyGeometryAndBvhRemainAvailableAfterInputsClose()
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
        byte[] payload = Payload(out int offset);
        using var bytes = new PageBuffer(arena, payload.Length);
        bytes.Write(0, payload);
        using var mesh = Mesh.Create("ready", Encoding, bytes, (ulong)offset);
        bytes.Dispose();
        arena.Dispose();
        Assert.Equal(1, mesh.PageCount);
        Assert.True(mesh.TryRetainPage(0, out PageRange<byte>? page));
        using (page)
        using (PageRange<byte> bvh = mesh.RetainBvh())
        {
            mesh.Dispose();
            Assert.Equal(payload[..offset], page.Sequence.ToArray());
            Assert.Equal(payload[offset..], bvh.Sequence.ToArray());
        }
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public void MissingPagesRequireExplicitSupplyAndBadPagesDoNotChangeTheMesh()
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
        byte[] payload = Payload(out int offset);
        using var bytes = new PageBuffer(arena, payload.Length);
        bytes.Write(0, payload);
        var descriptions = MeshIntegrity.Inspect(bytes, offset, Encoding);
        using PageBuffer bvh = bytes.Slice(offset, payload.Length - offset);
        using var mesh = Mesh.Create("partial", Encoding, descriptions, bvh);
        Assert.False(mesh.TryRetainPage(0, out _));
        Assert.Throws<KeyNotFoundException>(() => mesh.ReadPage(0, new byte[offset]));
        using var ready = new PageBufferMap(arena);
        using PageBuffer page = bytes.Slice(0, offset);
        ready.Set(0, page);
        ready.Write(0, offset - 1, new byte[] { 99 });
        Assert.Throws<InvalidDataException>(() => mesh.AddPages(ready, mesh.Revision));
        Assert.False(mesh.TryRetainPage(0, out _));
        ready.Set(0, page);
        mesh.AddPages(ready, mesh.Revision);
        byte[] restored = new byte[offset];
        mesh.ReadPage(0, restored);
        Assert.Equal(payload[..offset], restored);
        using var replacement = Mesh.Create("replacement", Encoding, bytes, (ulong)offset);
        ulong previousRevision = mesh.Revision;
        mesh.Update(replacement);
        Assert.Equal("replacement", mesh.Name);
        Assert.Equal(previousRevision + 1, mesh.Revision);
        Assert.Throws<InvalidOperationException>(() => mesh.AddPages(ready, previousRevision));
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
        var node = new ClusterBVHNode { NodeType = 1 };
        node.SetLeafData(0, 1);
        MemoryMarshal.Write(bytes.AsSpan(bvhOffset), in node);
        return bytes;
    }
}
