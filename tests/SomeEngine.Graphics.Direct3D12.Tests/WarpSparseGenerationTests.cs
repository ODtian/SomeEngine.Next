using SomeEngine.Graphics.Direct3D12;
using System.Reflection;
using Xunit;
using NativeSubresourceTiling = Silk.NET.Direct3D12.SubresourceTiling;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpSparseGenerationTests
{


    [Fact]
    public void Sparse_mapping_validates_heap_class_offset_and_ignored_linear_dimensions()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SparseResources? capability));
        Assert.NotNull(capability);

        ulong tileSize = capability.TileSizeInBytes;
        using Buffer resource = CreateSparseBuffer(backend, device, tileSize, 1);
        using Heap bufferHeap = CreateSparseHeap(backend, device, tileSize);
        using Heap textureHeap = CreateSparseTextureHeap(backend, device, tileSize);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        SparseTileRegion oneTile = new(
            new SparseTileCoordinate(0, 0, 0, 0),
            uint.MaxValue,
            uint.MaxValue,
            uint.MaxValue,
            1,
            Boxed: false);

        Assert.Throws<ArgumentException>(() => backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(resource, oneTile, SparseMappingType.Mapped, textureHeap, 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(
                resource,
                oneTile,
                SparseMappingType.Mapped,
                bufferHeap,
                (ulong)uint.MaxValue + 1)]));

        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(resource, oneTile, SparseMappingType.Mapped, bufferHeap, 0)]));
        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(resource, oneTile, SparseMappingType.Unmapped, null, 0)]));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void TextureSparseMappingCommandsAcceptBoxAndMipRegions()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SparseResources? capability));
        Assert.NotNull(capability);
        Assert.True(capability.Texture2DSupported);
        Assert.Equal(
            capability.Texture2DSupported,
            !capability.SupportedTexture2DFormats.IsEmpty);
        Assert.Equal(
            capability.Texture3DSupported,
            !capability.SupportedTexture3DFormats.IsEmpty);
        Assert.True(
            capability.SupportedTexture2DFormats.Contains(Format.R8G8B8A8UNorm));
        Assert.Throws<NotSupportedException>(() => backend.CreateReservedTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture1D,
                512,
                1,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled)));

        const uint extent = 512;
        const uint mipCount = 10;
        ulong tileSize = capability.TileSizeInBytes;
        using Texture texture = backend.CreateReservedTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                extent,
                extent,
                1,
                mipCount,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.CopySource | TextureUsages.CopyDestination | TextureUsages.Sampled));
        SparseResourceInfo info = backend.GetSparseResourceInfo(texture);
        Assert.True(info.TileShape.Width > 0);
        Assert.True(info.TileShape.Height > 0);
        Assert.True(info.PackedMips.StandardMipLevelCount > 0);
        Assert.Equal(mipCount, checked(
            info.PackedMips.StandardMipLevelCount +
            info.PackedMips.PackedMipLevelCount));

        uint mipWidthInTiles = DivideRoundUp(extent, info.TileShape.Width);
        uint mipHeightInTiles = DivideRoundUp(extent, info.TileShape.Height);
        Assert.True(mipWidthInTiles >= 2);
        Assert.True(mipHeightInTiles >= 2);
        SparseTileRegion box = new(
            new SparseTileCoordinate(0, 0, 0, 0),
            2,
            2,
            1,
            4,
            Boxed: true);
        using Heap boxedHeap = CreateSparseTextureHeap(
            backend,
            device,
            checked(tileSize * 4));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(texture, box, SparseMappingType.Mapped, boxedHeap, 0)]));

        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(texture, box, SparseMappingType.Unmapped, null, 0)]));

        SparseTileRegion spanning = new(
            new SparseTileCoordinate(
                mipWidthInTiles - 1,
                mipHeightInTiles - 1,
                0,
                0),
            0,
            0,
            0,
            2,
            Boxed: false);
        using Heap spanningHeap = CreateSparseTextureHeap(
            backend,
            device,
            checked(tileSize * 2));
        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(texture, spanning, SparseMappingType.Mapped, spanningHeap, 0)]));
        SparseTileRegion firstTileOfNextMip = new(
            new SparseTileCoordinate(0, 0, 0, 1),
            0,
            0,
            0,
            1,
            Boxed: false);

        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(texture, spanning, SparseMappingType.Unmapped, null, 0)]));

        if (info.PackedMips.PackedMipLevelCount != 0)
        {
            uint packedTileCount = info.PackedMips.PackedMipTileCount;
            Assert.True(packedTileCount > 0);
            using Heap packedHeap = CreateSparseTextureHeap(
                backend,
                device,
                checked(tileSize * packedTileCount));
            uint firstPackedMip = info.PackedMips.StandardMipLevelCount;
            SparseTileRegion packed = new(
                new SparseTileCoordinate(0, 0, 0, firstPackedMip),
                0,
                0,
                0,
                packedTileCount,
                Boxed: false);
            Wait(backend, backend.UpdateSparseMappings(
                queue,
                [new SparseMappingDesc(texture, packed, SparseMappingType.Mapped, packedHeap, 0)]));

            uint lastPackedMip = mipCount - 1;
            SparseTileRegion packedAlias = packed with
            {
                Start = new SparseTileCoordinate(0, 0, 0, lastPackedMip),
            };

            Wait(backend, backend.UpdateSparseMappings(
                queue,
                [new SparseMappingDesc(texture, packedAlias, SparseMappingType.Unmapped, null, 0)]));
        }
        else
        {
            Assert.Equal(0U, info.PackedMips.PackedMipTileCount);
            Assert.Equal(0U, info.PackedMips.PackedMipTileOffset);
        }
        backend.CollectCompleted(device);
    }

    [Fact]
    public void RemappingOneTileChangesOnlyThatTilesReadback()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SparseResources? sparse));
        Assert.NotNull(sparse);
        ulong tileSize = sparse.TileSizeInBytes;
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        using Buffer resource = CreateSparseBuffer(backend, device, tileSize, 3);
        using Buffer seed = CreateSparseBuffer(backend, device, tileSize, 1);
        using Heap first = CreateSparseHeap(backend, device, tileSize * 3);
        using Heap second = CreateSparseHeap(backend, device, tileSize);
        Wait(backend, backend.UpdateSparseMappings(queue,
            [new SparseMappingDesc(resource, LinearRegion(0, 3), SparseMappingType.Mapped, first, 0),
             new SparseMappingDesc(seed, LinearRegion(0, 1), SparseMappingType.Mapped, second, 0)]));
        WriteBytes(backend, device, queue, resource, tileSize * 3, 0x11);
        WriteBytes(backend, device, queue, seed, tileSize, 0x22);
        Wait(backend, backend.UpdateSparseMappings(queue,
            [new SparseMappingDesc(resource, LinearRegion(1, 1), SparseMappingType.Mapped, second, 0)]));
        byte[] actual = ReadBytes(backend, device, queue, resource, tileSize * 3);
        for (int i = 0; i < actual.Length; i++)
            Assert.Equal(i >= (int)tileSize && i < (int)(tileSize * 2) ? (byte)0x22 : (byte)0x11, actual[i]);
        Wait(backend, backend.UpdateSparseMappings(queue,
            [new SparseMappingDesc(resource, LinearRegion(1, 1), SparseMappingType.Unmapped, null, 0)]));
        Wait(backend, backend.UpdateSparseMappings(queue,
            [new SparseMappingDesc(resource, LinearRegion(1, 1), SparseMappingType.Mapped, first, 1)]));
        Assert.All(ReadBytes(backend, device, queue, resource, tileSize * 3), value => Assert.Equal((byte)0x11, value));
    }


    [Fact]
    public void SparseMappingCopyCommandsCompleteWithSelfOverlap()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SparseResources? capability));
        Assert.NotNull(capability);

        ulong tileSize = capability.TileSizeInBytes;
        using Buffer source = CreateSparseBuffer(backend, device, tileSize, 3);
        using Buffer middle = CreateSparseBuffer(backend, device, tileSize, 3);
        using Buffer destination = CreateSparseBuffer(backend, device, tileSize, 3);
        using Heap first = CreateSparseHeap(backend, device, tileSize);
        using Heap second = CreateSparseHeap(backend, device, tileSize);
        using Heap overwritten = CreateSparseHeap(backend, device, checked(tileSize * 3));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        SparseTileRegion whole = LinearRegion(0, 3);

        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [
                new SparseMappingDesc(
                    source,
                    LinearRegion(0, 1),
                    SparseMappingType.Mapped,
                    first,
                    0),
                new SparseMappingDesc(
                    source,
                    LinearRegion(2, 1),
                    SparseMappingType.Mapped,
                    second,
                    0),
                new SparseMappingDesc(
                    middle,
                    whole,
                    SparseMappingType.Mapped,
                    overwritten,
                    0),
                new SparseMappingDesc(
                    destination,
                    whole,
                    SparseMappingType.Mapped,
                    overwritten,
                    0),
            ]));

        SparseTileCoordinate origin = new(0, 0, 0, 0);
        Wait(backend, backend.CopySparseMappings(
            queue,
            [
                new SparseMappingCopyDesc(middle, origin, source, origin, whole),
                new SparseMappingCopyDesc(destination, origin, middle, origin, whole),
            ]));

        SparseTileRegion twoTiles = LinearRegion(0, 2);
        Wait(backend, backend.CopySparseMappings(
            queue,
            [new SparseMappingCopyDesc(
                destination,
                new SparseTileCoordinate(1, 0, 0, 0),
                destination,
                origin,
                twoTiles)]));

        backend.CollectCompleted(device);
    }

    [Fact]
    public void SubmittedSparseCommandsCompleteAfterUnmapAndHeapDisposal()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SparseResources? capability));
        Assert.NotNull(capability);

        ulong tileSize = capability.TileSizeInBytes;
        using Buffer resource = CreateSparseBuffer(backend, device, tileSize, 1);
        Heap heap = CreateSparseHeap(backend, device, tileSize);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        SparseTileRegion oneTile = LinearRegion(0, 1);

        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(resource, oneTile, SparseMappingType.Mapped, heap, 0)]));
        backend.CollectCompleted(device);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context, default);
        backend.Barrier(context, new BufferBarrier(
            resource,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopySource));
        RecordedCommands commands = backend.End(context);
        QueueCompletion submitted = Submit(backend, queue, commands);
        commands.Dispose();

        QueueCompletion unmapped = backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(resource, oneTile, SparseMappingType.Unmapped, null, 0)]);
        heap.Dispose();

        Wait(backend, unmapped);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(submitted, TimeSpan.Zero));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void ConcurrentSparseMappingCommandsComplete()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SparseResources? capability));
        Assert.NotNull(capability);
        ulong tileSize = capability.TileSizeInBytes;
        using Buffer firstResource = CreateSparseBuffer(backend, device, tileSize, 1);
        using Buffer secondResource = CreateSparseBuffer(backend, device, tileSize, 1);
        using Heap firstHeap = CreateSparseHeap(backend, device, tileSize);
        using Heap secondHeap = CreateSparseHeap(backend, device, tileSize);
        Queue firstQueue = backend.GetQueue(device, QueueType.Copy);
        Queue secondQueue = backend.GetQueue(device, QueueType.Graphics);
        SparseTileRegion tile = LinearRegion(0, 1);
        SparseMappingDesc[] firstMapping =
            [new(firstResource, tile, SparseMappingType.Mapped, firstHeap, 0)];
        SparseMappingDesc[] secondMapping =
            [new(secondResource, tile, SparseMappingType.Mapped, secondHeap, 0)];
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);
        Exception? firstFailure = null;
        Exception? secondFailure = null;

        Thread first = new(() => Run(firstQueue, firstMapping, failure => firstFailure = failure));
        Thread second = new(() => Run(secondQueue, secondMapping, failure => secondFailure = failure));
        first.Start();
        second.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        start.Set();
        Assert.True(first.Join(TimeSpan.FromSeconds(10)));
        Assert.True(second.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(firstFailure);
        Assert.Null(secondFailure);
        backend.CollectCompleted(device);

        void Run(
            Queue queue,
            SparseMappingDesc[] mapping,
            Action<Exception> recordFailure)
        {
            ready.Signal();
            start.Wait();
            try
            {
                Wait(backend, backend.UpdateSparseMappings(queue, mapping));
            }
            catch (Exception exception)
            {
                recordFailure(exception);
            }
        }
    }

    [Fact]
    public void InvalidSparseMappingBatchIsRejectedAndCorrectedBatchCompletes()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SparseResources? capability));
        Assert.NotNull(capability);
        ulong tileSize = capability.TileSizeInBytes;
        using Buffer resource = CreateSparseBuffer(backend, device, tileSize, 2);
        Heap heap = CreateSparseHeap(backend, device, checked(tileSize * 2));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        SparseTileRegion first = LinearRegion(0, 1);
        SparseTileRegion second = LinearRegion(1, 1);
        SparseMappingDesc[] mappings =
        [
            new(resource, first, SparseMappingType.Mapped, heap, 0),
            new(resource, second, SparseMappingType.Mapped, heap, 2),
        ];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.UpdateSparseMappings(queue, mappings));

        mappings[1] = new(resource, second, SparseMappingType.Mapped, heap, 1);
        Wait(backend, backend.UpdateSparseMappings(queue, mappings));
        heap.Dispose();
        backend.CollectCompleted(device);
    }

    private static Buffer CreateSparseBuffer(
        IGraphicsBackend backend,
        Device device,
        ulong tileSize,
        ulong tileCount) =>
        backend.CreateReservedBuffer(
            device,
            new BufferDesc(
                checked(tileSize * tileCount),
                BufferUsages.CopySource | BufferUsages.CopyDestination));

    private static Heap CreateSparseHeap(
        IGraphicsBackend backend,
        Device device,
        ulong size) =>
        backend.CreateHeap(
            device,
            new HeapDesc(size, 64 * 1024, MemoryType.DeviceLocal, HeapFlags.Buffers));

    private static Heap CreateSparseTextureHeap(
        IGraphicsBackend backend,
        Device device,
        ulong size) =>
        backend.CreateHeap(
            device,
            new HeapDesc(size, 64 * 1024, MemoryType.DeviceLocal, HeapFlags.Textures));

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((uint)(((ulong)value + divisor - 1) / divisor));

    private static SparseTileRegion LinearRegion(uint start, uint tileCount) =>
        new(new SparseTileCoordinate(start, 0, 0, 0), 0, 0, 0, tileCount, Boxed: false);


    private static QueueCompletion Submit(
        IGraphicsBackend backend,
        Queue queue,
        RecordedCommands commands)
    {
        RecordedCommands[] commandLists = [commands];
        return backend.Submit(queue, new QueueSubmitDesc([], [], commandLists, [], []));
    }

    private static void Wait(IGraphicsBackend backend, QueueCompletion completion) =>
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));


    private static void WriteBytes(IGraphicsBackend backend, Device device, Queue queue, Buffer destination, ulong size, byte value)
    {
        using Buffer upload = backend.CreateBuffer(device, new BufferDesc(size, BufferUsages.CopySource), MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            mapped.Bytes.Fill(value);
            mapped.Flush(mapped.Range);
        }
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(destination, PipelineSync.None, PipelineSync.Copy, ResourceAccess.NoAccess, ResourceAccess.CopyDestination));
        backend.CopyBuffer(context, new BufferCopy(upload, 0, destination, 0, size));
        using RecordedCommands commands = backend.End(context);
        Wait(backend, Submit(backend, queue, commands));
    }

    private static byte[] ReadBytes(IGraphicsBackend backend, Device device, Queue queue, Buffer source, ulong size)
    {
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(size, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(source, PipelineSync.None, PipelineSync.Copy, ResourceAccess.NoAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(source, 0, readback, 0, size));
        using RecordedCommands commands = backend.End(context);
        Wait(backend, Submit(backend, queue, commands));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        return mapped.Bytes.ToArray();
    }
}
