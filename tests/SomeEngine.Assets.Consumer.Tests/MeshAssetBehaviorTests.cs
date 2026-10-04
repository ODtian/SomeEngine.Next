using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Pipeline;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class MeshAssetBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationsFromTheSameBlocksSharePagesAndOutliveTheAsset(bool fileBacked)
    {
        using var files = new MeshFiles();
        using var created = CreateAsset();
        if (fileBacked) await created.SaveAsync(files.Path);
        using var asset = fileBacked ? await MeshAsset.OpenAsync(files.Path) : created.Duplicate();
        ClusterGeometryBlock geometry = asset.GetBlock<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock attributes = await asset.GetBlockAsync<ClusterVertexAttributesBlock>();
        using var branch = geometry.Duplicate();
        var first = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), attributes, "first");
        var second = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), attributes, "second");
        var third = await branch.CreateAsync(asset.GetBlock<MeshBlock>(), attributes, "branch");
        Assert.True(first.TryGetPage(0, out var firstPage));
        Assert.True(second.TryGetPage(0, out var secondPage));
        Assert.True(third.TryGetPage(0, out var thirdPage));
        Assert.True(firstPage.Equals(secondPage)); Assert.True(firstPage.Equals(thirdPage));
        Assert.True(first.Bvh.Equals(second.Bvh));
        using MeshPayloadSource source = await geometry.OpenPayloadSourceAsync(attributes);
        asset.Dispose();
        Assert.Equal(firstPage.ToArray(), (await source.ReadPageAsync(0)).ToArray());
        Assert.Equal("surface", Assert.Single(first.Regions).Name);
    }

    [Fact]
    public async Task EditingGeometryKeepsRetainedPagesAndNewCreationsUseNewData()
    {
        using var asset = CreateAsset();
        ClusterGeometryBlock geometry = asset.GetBlock<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock attributes = asset.GetBlock<ClusterVertexAttributesBlock>();
        using var first = await geometry.OpenPayloadSourceAsync(attributes);
        byte[] original = (await first.ReadPageAsync(0)).ToArray();
        byte[] data = geometry.Clusters[0].PositionData.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(data, 7);
        geometry.Clusters[0] = geometry.Clusters[0] with { PositionData = [.. data], BoundMin = new() { X = 7 }, BoundMax = new() { X = 8, Y = 1 } };
        using var changed = await geometry.OpenPayloadSourceAsync(attributes);
        Assert.Equal(original, (await first.ReadPageAsync(0)).ToArray());
        Assert.NotEqual(original, (await changed.ReadPageAsync(0)).ToArray());
        Assert.Equal(7, geometry.Clusters[0].DecodePosition(0).X);
    }

    [Fact]
    public async Task PositionHeaderEditsReachCpuPagesAndReopenedAssets()
    {
        using var files = new MeshFiles();
        using var created = CreateAsset();
        ClusterGeometryBlock geometry = created.GetBlock<ClusterGeometryBlock>();
        byte[] data = geometry.Clusters[0].PositionData.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(data, 7);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(12), 2);
        geometry.Clusters[0] = geometry.Clusters[0] with { PositionData = [.. data] };
        await created.SaveAsync(files.Path);
        using var reopened = await MeshAsset.OpenAsync(files.Path);
        foreach (MeshAsset asset in new[] { created, reopened })
        {
            ClusterGeometryBlock block = await asset.GetBlockAsync<ClusterGeometryBlock>();
            ClusterGeometry cluster = Assert.Single(block.Clusters);
            Assert.Equal(7, cluster.PositionOrigin.X);
            Assert.Equal(2, cluster.PositionStep);
            Assert.Equal(new Vector3(7, 0, 0), cluster.DecodePosition(0));
            Assert.Equal(new Vector3(9, 0, 0), cluster.DecodePosition(1));
            Assert.Equal(new Vector3(7, 2, 0), cluster.DecodePosition(2));
            using var source = await block.OpenPayloadSourceAsync(await asset.GetBlockAsync<ClusterVertexAttributesBlock>());
            ReadOnlyMemory<byte> page = await source.ReadPageAsync(0);
            var header = System.Runtime.InteropServices.MemoryMarshal.Read<SomeEngine.Resources.Geometry.MeshPageHeader>(page.Span);
            var packed = System.Runtime.InteropServices.MemoryMarshal.Read<SomeEngine.Resources.Geometry.PackedCluster>(page.Span[(int)header.ClustersOffset..]);
            int descriptor = (int)(header.VertexBlobsOffset + packed.VertexBlobOffset);
            uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(page.Span[descriptor..]);
            Assert.Equal(data, page.Slice(descriptor + (int)offset, data.Length).ToArray());
        }
    }

    [Fact]
    public async Task EditingAttributesKeepsRetainedPagesAndNextReadUsesNewBytes()
    {
        using var asset = CreateAsset();
        ClusterGeometryBlock geometry = asset.GetBlock<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock attributes = asset.GetBlock<ClusterVertexAttributesBlock>();
        using var first = await geometry.OpenPayloadSourceAsync(attributes);
        byte[] original = (await first.ReadPageAsync(0)).ToArray();
        attributes.Clusters[0] = new() { Data = [4, 3, 2, 1] };
        using var changed = await geometry.OpenPayloadSourceAsync(attributes);
        Assert.Equal(original, (await first.ReadPageAsync(0)).ToArray());
        Assert.NotEqual(original, (await changed.ReadPageAsync(0)).ToArray());
    }

    [Fact]
    public async Task DifferentVertexIdentitiesAreRejectedBeforeCreatingRenderGeometry()
    {
        using var asset = CreateAsset();
        ClusterGeometryBlock geometry = asset.GetBlock<ClusterGeometryBlock>();
        using var other = asset.GetBlock<ClusterVertexAttributesBlock>().Duplicate();
        other.VertexIdentity = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidDataException>(() => geometry.CreateAsync(asset.GetBlock<MeshBlock>(), other, "mismatch").AsTask());
    }

    [Fact]
    public async Task ReopenedBlocksLoadPagesIntoTheSameRenderGeometry()
    {
        using var files = new MeshFiles();
        using var created = CreateAsset();
        await created.SaveAsync(files.Path);
        using var first = await MeshAsset.OpenAsync(files.Path);
        var mesh = await first.GetBlock<ClusterGeometryBlock>().CreateAsync(first.GetBlock<MeshBlock>(),
            await first.GetBlockAsync<ClusterVertexAttributesBlock>(), first.Name);
        using var second = await MeshAsset.OpenAsync(files.Path);
        await second.GetBlock<ClusterGeometryBlock>().LoadPagesAsync(mesh, await second.GetBlockAsync<ClusterVertexAttributesBlock>(), [0]);
        Assert.True(mesh.TryGetPage(0, out _));
        using var source = MeshPayloadSource.FromMesh(mesh);
        Assert.True(source.Matches(mesh));
        using var changedAsset = CreateAsset();
        ClusterGeometryBlock changed = changedAsset.GetBlock<ClusterGeometryBlock>();
        byte[] data = changed.Clusters[0].PositionData.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(data, 8);
        changed.Clusters[0] = changed.Clusters[0] with { PositionData = [.. data], BoundMin = new() { X = 8 }, BoundMax = new() { X = 9, Y = 1 } };
        var replacement = await changed.CreateAsync(changedAsset.GetBlock<MeshBlock>(), changedAsset.GetBlock<ClusterVertexAttributesBlock>(), "replacement");
        Assert.False(source.Matches(replacement));
        byte[] before = (await source.ReadPageAsync(0)).ToArray();
        mesh.Update(replacement);
        Assert.Equal(before, (await source.ReadPageAsync(0)).ToArray());
    }

    [Fact]
    public async Task LargeAttributeSetsRetainEveryStreamInTheRenderSource()
    {
        using var asset = CreateAsset();
        ClusterGeometryBlock geometry = asset.GetBlock<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock attributes = asset.GetBlock<ClusterVertexAttributesBlock>();
        var streams = Enumerable.Range(0, 512).Select(index => new ClusterVertexAttribute
        {
            Name = "_ATTRIBUTE_" + index,
            ComponentCount = 64,
            VertexComponentCounts = [64, 64, 64],
            Data = [.. new byte[3 * 64 * sizeof(double)]],
        }).ToArray();
        attributes.Clusters[0] = new ClusterVertexAttributes { Attributes = [.. streams], Data = [.. streams.SelectMany(stream => stream.Data)] };
        var mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), attributes, "large attributes");
        using var source = await geometry.OpenPayloadSourceAsync(attributes);
        Assert.True(mesh.TryGetPage(0, out var page));
        Assert.True(page.Length > 131072);
        Assert.Equal(page.ToArray(), (await source.ReadPageAsync(0)).ToArray());
        Assert.Equal(512, attributes.Clusters[0].Attributes.Length);
        Assert.All(attributes.Clusters[0].Attributes, stream => Assert.Equal(0, stream.ReadComponent(2, 63)));
    }

    [Fact]
    public async Task CustomPositionBytesAndProducerBoundsReachTheRendererUnchanged()
    {
        using var asset = CreateAsset();
        ClusterGeometryBlock geometry = asset.GetBlock<ClusterGeometryBlock>();
        geometry.PositionEncoding = "example.positions.float32.v1";
        float[] positions = [0,0,0, 1,0,0, 0,1,0];
        byte[] bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(positions.AsSpan()).ToArray();
        geometry.Clusters[0] = geometry.Clusters[0] with { PositionData = [.. bytes], BoundMin = new(), BoundMax = new() { X = 1, Y = 1 } };
        using var source = await geometry.OpenPayloadSourceAsync(asset.GetBlock<ClusterVertexAttributesBlock>());
        ReadOnlyMemory<byte> page = await source.ReadPageAsync(0);
        var header = System.Runtime.InteropServices.MemoryMarshal.Read<SomeEngine.Resources.Geometry.MeshPageHeader>(page.Span);
        var cluster = System.Runtime.InteropServices.MemoryMarshal.Read<SomeEngine.Resources.Geometry.PackedCluster>(page.Span[(int)header.ClustersOffset..]);
        Assert.Equal(Vector3.Zero, cluster.BoundMin);
        Assert.Equal(new Vector3(1,1,0), cluster.BoundMax);
        int descriptor = (int)(header.VertexBlobsOffset + cluster.VertexBlobOffset);
        uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(page.Span[descriptor..]);
        Assert.Equal(bytes, page.Slice(descriptor + (int)offset, bytes.Length).ToArray());
    }

    private static MeshAsset CreateAsset()
    {
        var asset = new MeshAsset { Name = "mesh" };
        using var shape = new MeshBlock { Regions = [new SomeEngine.Resources.MeshRegion { Name = "surface" }] };
        using var geometry = new ClusterGeometryBlock
        {
            Vertices = [new(0, 0, 0), new(0, 1, 1), new(0, 2, 2)],
            Clusters = [new() { Vertices = [0, 1, 2], Indices = [0, 1, 2], TriangleMaterials = [0],
                PositionData = [0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,128,63, 0,0,0,0,0,0, 1,0,0,0,0,0, 0,0,1,0,0,0],
                BoundMin = new(), BoundMax = new() { X = 1, Y = 1 }, ParentError = float.MaxValue, Group = -1, ParentGroup = -1,
                Radius = 2, LodRadius = 2, SelfLodRadius = 2 }],
        };
        using var attributes = new ClusterVertexAttributesBlock { VertexIdentity = geometry.VertexIdentity, Clusters = [new ClusterVertexAttributes { Data = [.. new byte[16]] }] };
        asset.SetBlock(shape); asset.SetBlock(geometry); asset.SetBlock(attributes); return asset;
    }
    private sealed class MeshFiles : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SomeEngine-cluster-" + Guid.NewGuid().ToString("N"));
        public MeshFiles() => Directory.CreateDirectory(_directory);
        public string Path => System.IO.Path.Combine(_directory, "mesh.mesh.asset");
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
