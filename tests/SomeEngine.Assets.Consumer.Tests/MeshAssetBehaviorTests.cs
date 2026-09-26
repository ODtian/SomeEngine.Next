using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class MeshAssetBehaviorTests
{
    private const string Encoding = "consumer.opaque-vertices";

    [Fact]
    public async Task ProceduralSourceKeepsItsPagesAcrossMeshUpdatesAndDisposal()
    {
        byte[] payload = CreatePayload(out int offset);
        using var mesh = SomeEngine.Resources.Mesh.Create("ready", Encoding, payload.AsMemory(), (ulong)offset);
        using var source = MeshPayloadSource.FromMesh(mesh);
        Assert.True(source.Matches(mesh));
        payload[offset - 4] = 23;
        using var changed = SomeEngine.Resources.Mesh.Create("changed", Encoding, payload.AsMemory(), (ulong)offset);
        Assert.False(source.Matches(changed));
        mesh.Update(changed);
        mesh.Dispose();
        byte[] result = new byte[offset];
        await source.ReadPageIntoAsync(0, result);
        Assert.Equal(0, result[offset - 4]);
        using var page = await source.AcquirePageAsync(0, PageArena.Shared);
        Assert.Equal(0, page[offset - 4]);
        using var asset = CreateAsset(payload, offset);
        using var partial = await asset.GetBlock<ClusterMeshBlock>().CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        Assert.Throws<InvalidOperationException>(() => MeshPayloadSource.FromMesh(partial));
    }

    [Fact]
    public async Task CreatingMeshAcquiresOnlyBvhAndExplicitPagesOutliveTheAsset()
    {
        using var files = new MeshFiles();
        byte[] payload = CreatePayload(out int offset);
        using (var created = CreateAsset(payload, offset)) await created.SaveAsync(files.Path);
        using var arena = new PageArena(new ResidencyBudgetLedger());
        using var asset = await MeshAsset.OpenAsync(files.Path, arena);
        using var mesh = await asset.GetBlock<ClusterMeshBlock>().CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        Assert.Equal("surface", Assert.Single(mesh.Regions).Name);
        Assert.False(mesh.TryRetainPage(0, out _));
        Assert.True(arena.Residency.Used(ResidencyClass.DecodedCpu) < offset);
        byte[] bvh = new byte[mesh.BvhByteLength];
        mesh.ReadBvh(bvh);
        Assert.Equal(payload[offset..], bvh);
        // A second decode creates different digest arrays with the same content.
        using (var reopened = await MeshAsset.OpenAsync(files.Path, arena))
            await reopened.GetBlock<ClusterMeshBlock>().LoadPagesAsync(mesh, [0]);
        Assert.True(mesh.TryRetainPage(0, out var read));
        using (read)
        {
            asset.Dispose();
            mesh.Dispose();
            Assert.Equal(payload[..offset], read.ToArray());
        }
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public async Task RetainedSourceSurvivesFileClosureAndResidentEditsSeparateItsBytes()
    {
        using var files = new MeshFiles();
        byte[] payload = CreatePayload(out int offset);
        using var created = CreateAsset(payload, offset);
        using var resident = await created.GetBlock<ClusterMeshBlock>().OpenPayloadSourceAsync();
        created.GetBlock<SomeEngine.Assets.Schema.ClusterMeshBlock>().Chunks.Write(SomeEngine.Assets.Schema.ClusterMeshBlock.PayloadChunkKey, offset - 4, new byte[] { 39 });
        using var edited = await created.GetBlock<ClusterMeshBlock>().OpenPayloadSourceAsync();
        await created.SaveAsync(files.Path);
        created.Dispose();
        byte[] original = new byte[offset];
        byte[] changed = new byte[offset];
        await resident.ReadPageIntoAsync(0, original);
        await edited.ReadPageIntoAsync(0, changed);
        Assert.Equal(payload[..offset], original);
        Assert.Equal(39, changed[offset - 4]);
        using var opened = await MeshAsset.OpenAsync(files.Path);
        using var disk = await opened.GetBlock<ClusterMeshBlock>().OpenPayloadSourceAsync();
        opened.Dispose();
        await disk.ReadPageIntoAsync(0, original);
        Assert.Equal(changed, original);
    }

    [Fact]
    public async Task CorruptGeometryIsRejectedWhenExplicitlyReadAndDoesNotChangeMesh()
    {
        using var files = new MeshFiles();
        byte[] payload = CreatePayload(out int offset);
        using (var created = CreateAsset(payload, offset)) await created.SaveAsync(files.Path);
        long chunkOffset;
        using var description = await MeshAsset.OpenAsync(files.Path);
        ulong payloadKey = description.BlockDescriptions.Single(block => block.BlockTypeId == ClusterMeshBlock.BlockTypeId)
            .GetStorageKey(ClusterMeshBlock.PayloadChunkKey);
        description.Dispose();
        await using (var document = await BinaryDocument.OpenAsync(FileRangeSource.Open(files.Path), MeshAsset.WireType, ownsSource: true))
            chunkOffset = (await document.FindChunkAsync(payloadKey))!.Value.Offset;
        using (var stream = new FileStream(files.Path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = chunkOffset + offset - 4;
            stream.WriteByte(73);
        }
        using var asset = await MeshAsset.OpenAsync(files.Path);
        using var mesh = await asset.GetBlock<ClusterMeshBlock>().CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        Assert.False(mesh.TryRetainPage(0, out _));
        await Assert.ThrowsAsync<InvalidDataException>(() => asset.GetBlock<ClusterMeshBlock>().LoadPagesAsync(mesh, [0]).AsTask());
        Assert.False(mesh.TryRetainPage(0, out _));
        using var bvh = mesh.RetainBvh();
        Assert.Equal(payload[offset..], bvh.ToArray());
    }

    private static MeshAsset CreateAsset(byte[] payload, int offset)
    {
        var result = new MeshAsset { Name = "mesh" };
        using var geometry = new MeshBlock { Regions = [new SomeEngine.Resources.MeshRegion { Name = "surface" }] };
        using var clusters = new ClusterMeshBlock { VertexEncoding = Encoding };
        try
        {
            using var bytes = new PageBuffer(result.Pages, payload.Length);
            bytes.Write(0, payload);
            clusters.SetPayload(bytes, (ulong)offset);
            result.SetBlock(geometry);
            result.SetBlock(clusters);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static byte[] CreatePayload(out int bvhOffset)
    {
        int vertices = MeshPageHeader.Size + PackedCluster.SizeInBytes;
        const int vertexBytes = 65536;
        bvhOffset = vertices + vertexBytes + 3;
        byte[] bytes = new byte[bvhOffset + Unsafe.SizeOf<ClusterBVHNode>()];
        var header = new MeshPageHeader
        {
            ClusterCount = 1, TotalVertexCount = 3, TotalTriangleCount = 1, VertexBlobBytes = vertexBytes,
            ClustersOffset = MeshPageHeader.Size, VertexBlobsOffset = (uint)vertices,
            IndicesOffset = (uint)(vertices + vertexBytes), ExtensionDataOffset = (uint)bvhOffset,
            VertexEncodingHash = VertexEncodingCompatibility.Hash(Encoding),
        };
        MemoryMarshal.Write(bytes, in header);
        var cluster = new PackedCluster
        {
            VertexBlobSize = vertexBytes, ExtensionOffset = uint.MaxValue, MaterialTableOffset = uint.MaxValue,
            PackedCounts = 3 | (1u << 8),
        };
        MemoryMarshal.Write(bytes.AsSpan(MeshPageHeader.Size), in cluster);
        bytes[bvhOffset - 2] = 1; bytes[bvhOffset - 1] = 2;
        var node = new ClusterBVHNode { NodeType = 1 };
        node.SetLeafData(0, 1);
        MemoryMarshal.Write(bytes.AsSpan(bvhOffset), in node);
        return bytes;
    }

    private sealed class MeshFiles : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "someengine-mesh-" + Guid.NewGuid().ToString("N"));
        internal MeshFiles() => Directory.CreateDirectory(_directory);
        internal string Path => System.IO.Path.Combine(_directory, "test.mesh.asset");
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
