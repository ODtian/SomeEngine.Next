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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationsFromTheSameDefinitionShareGeometry(bool fileBacked)
    {
        using var files = new MeshFiles();
        byte[] payload = CreatePayload(out int offset);
        using var created = CreateAsset(payload, offset);
        if (fileBacked) await created.SaveAsync(files.Path);
        using var asset = fileBacked ? await MeshAsset.OpenAsync(files.Path) : created.Duplicate();
        ClusterMeshBlock block = asset.GetBlock<ClusterMeshBlock>();
        MeshBlock shape = asset.GetBlock<MeshBlock>();
        using var branch = block.Duplicate();

        var first = await block.CreateAsync(shape, "first");
        var second = await block.CreateAsync(shape, "second");
        var third = await branch.CreateAsync(shape, "branch");

        Assert.True(first.TryGetPage(0, out var firstPage));
        Assert.True(second.TryGetPage(0, out var secondPage));
        Assert.True(third.TryGetPage(0, out var thirdPage));
        Assert.True(firstPage.Equals(secondPage));
        Assert.True(firstPage.Equals(thirdPage));
        Assert.True(first.Bvh.Equals(second.Bvh));
        Assert.True(first.Bvh.Equals(third.Bvh));
    }

    [Fact]
    public async Task EditedPageDescriptionIsUsedByTheNextRead()
    {
        byte[] payload = CreatePayload(out int offset);
        using var asset = CreateAsset(payload, offset);
        ClusterMeshBlock block = asset.GetBlock<ClusterMeshBlock>();
        using var first = await block.OpenPayloadSourceAsync();
        ReadOnlyMemory<byte> bytes = await first.ReadPageAsync(0);
        block.PageDigests[0] = block.PageDigests[0] with { Sha256 = [.. new byte[32]] };
        using var changed = await block.OpenPayloadSourceAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => changed.ReadPageAsync(0).AsTask());
        Assert.Equal(payload[..offset], bytes.ToArray());
    }

    [Fact]
    public async Task ProceduralSourceKeepsItsPagesAcrossMeshUpdates()
    {
        byte[] payload = CreatePayload(out int offset);
        var mesh = SomeEngine.Resources.Mesh.Create("ready", Encoding, payload.AsMemory(), (ulong)offset);
        using var source = MeshPayloadSource.FromMesh(mesh);
        Assert.True(source.Matches(mesh));
        payload[offset - 4] = 23;
        var changed = SomeEngine.Resources.Mesh.Create("changed", Encoding, payload.AsMemory(), (ulong)offset);
        Assert.False(source.Matches(changed));
        mesh.Update(changed);
        byte[] result = new byte[offset];
        await source.ReadPageIntoAsync(0, result);
        Assert.Equal(0, result[offset - 4]);
        var page = await source.ReadPageAsync(0);
        Assert.Equal(0, page.Span[offset - 4]);
        using var asset = CreateAsset(payload, offset);
        var partial = SomeEngine.Resources.Mesh.Create("partial", Encoding, mesh.PageDigests, mesh.Bvh);
        Assert.Throws<InvalidOperationException>(() => MeshPayloadSource.FromMesh(partial));
    }

    [Fact]
    public async Task CreatingMeshAcquiresMinimumGeometryAndDataOutlivesTheAsset()
    {
        using var files = new MeshFiles();
        byte[] payload = CreatePayload(out int offset);
        using (var created = CreateAsset(payload, offset)) await created.SaveAsync(files.Path);
        using var asset = await MeshAsset.OpenAsync(files.Path);
        var mesh = await asset.GetBlock<ClusterMeshBlock>().CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        Assert.Equal("surface", Assert.Single(mesh.Regions).Name);
        Assert.True(mesh.TryGetPage(0, out _));
        byte[] bvh = new byte[mesh.BvhByteLength];
        mesh.ReadBvh(bvh);
        Assert.Equal(payload[offset..], bvh);
        // A second decode creates different digest arrays with the same content.
        using (var reopened = await MeshAsset.OpenAsync(files.Path))
            await reopened.GetBlock<ClusterMeshBlock>().LoadPagesAsync(mesh, [0]);
        Assert.True(mesh.TryGetPage(0, out var read));
        asset.Dispose();
        Assert.Equal(payload[..offset], read.ToArray());
    }

    [Fact]
    public async Task RetainedSourceSurvivesFileClosureAndResidentEditsSeparateItsBytes()
    {
        using var files = new MeshFiles();
        byte[] payload = CreatePayload(out int offset);
        using var created = CreateAsset(payload, offset);
        using var resident = await created.GetBlock<ClusterMeshBlock>().OpenPayloadSourceAsync();
        byte[] replacement = payload.ToArray();
        replacement[offset - 4] = 39;
        created.GetBlock<ClusterMeshBlock>().SetPayload(replacement, (ulong)offset);
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
        ClusterMeshBlock block = asset.GetBlock<ClusterMeshBlock>();
        await Assert.ThrowsAsync<InvalidDataException>(() => block.CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name).AsTask());
        using MeshPayloadSource source = await block.OpenPayloadSourceAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ReadPageAsync(0).AsTask());
        Assert.Equal(payload[offset..], (await source.ReadBvhAsync()).ToArray());
    }

    private static MeshAsset CreateAsset(byte[] payload, int offset)
    {
        var result = new MeshAsset { Name = "mesh" };
        using var geometry = new MeshBlock { Regions = [new SomeEngine.Resources.MeshRegion { Name = "surface" }] };
        using var clusters = new ClusterMeshBlock { VertexEncoding = Encoding };
        try
        {
            clusters.SetPayload(payload, (ulong)offset);
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
        var node = new ClusterBVHNode();
        var child = new ClusterHierarchySlice { PageRangeKey = 1u, GroupPartData = (1u << 16) | 0xFFFFu };
        child.SetErrors(0, float.MaxValue);
        node.SetChild(0, child);
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
