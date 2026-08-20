using SomeEngine.Graphics.Direct3D12;
using System.Reflection;
using Xunit;
using NativeSubresourceTiling = Silk.NET.Direct3D12.SubresourceTiling;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpSparseGenerationTests
{
    [Fact]
    public void Packed_mip_aliases_form_one_logical_segment_per_array_group()
    {
        SparseResourceInfo info = new(
            new SparseTileShape(128, 128, 1),
            16,
            new SparsePackedMipInfo(2, 2, 6, 2),
            64 * 1024);
        NativeSubresourceTiling[] tilings =
        [
            new(2, 2, 1, 0),
            new(1, 2, 1, 4),
            new(0, 0, 0, uint.MaxValue),
            new(0, 0, 0, uint.MaxValue),
            new(2, 2, 1, 8),
            new(1, 2, 1, 12),
            new(0, 0, 0, uint.MaxValue),
            new(0, 0, 0, uint.MaxValue),
        ];

        SparseTileRegion alias = new(
            new SparseTileCoordinate(1, 0, 0, 3),
            0,
            0,
            0,
            1,
            Boxed: false);
        var aliasIntervals = NormalizeSparseRegion(
            info,
            tilings,
            8,
            alias);
        Assert.Equal([(2U, 1UL, 1UL)], aliasIntervals);

        SparseTileRegion spanning = new(
            new SparseTileCoordinate(0, 1, 0, 1),
            0,
            0,
            0,
            4,
            Boxed: false);
        var spanningIntervals = NormalizeSparseRegion(
            info,
            tilings,
            8,
            spanning);
        Assert.Equal(
            [(1U, 1UL, 1UL), (2U, 0UL, 2UL), (4U, 0UL, 1UL)],
            spanningIntervals);

        SparseTileRegion packedBox = new(
            new SparseTileCoordinate(0, 0, 0, 2),
            1,
            1,
            1,
            1,
            Boxed: true);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NormalizeSparseRegion(info, tilings, 8, packedBox));
    }

    [Fact]
    public void Sparse_mapping_validates_heap_class_offset_and_ignored_linear_dimensions()
    {
        using var backend = new D3D12Backend();
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
        Assert.Equal(1UL, D3D12PrivateState.CountSparseMappingTiles(resource, oneTile, null));

        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(resource, oneTile, SparseMappingType.Mapped, bufferHeap, 0)]));
        Assert.Equal(1UL, D3D12PrivateState.CountSparseMappingTiles(resource, oneTile, bufferHeap));
        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(resource, oneTile, SparseMappingType.Unmapped, null, 0)]));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Texture_generation_normalizes_box_rows_and_subresource_spans()
    {
        using var backend = new D3D12Backend();
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
        Assert.Equal(4UL, D3D12PrivateState.CountSparseMappingTiles(texture, box, boxedHeap));

        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(texture, box, SparseMappingType.Unmapped, null, 0)]));
        Assert.Equal(4UL, D3D12PrivateState.CountSparseMappingTiles(texture, box, null));

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
        Assert.Equal(2UL, D3D12PrivateState.CountSparseMappingTiles(texture, spanning, spanningHeap));
        SparseTileRegion firstTileOfNextMip = new(
            new SparseTileCoordinate(0, 0, 0, 1),
            0,
            0,
            0,
            1,
            Boxed: false);
        Assert.Equal(
            1UL,
            D3D12PrivateState.CountSparseMappingTiles(texture, firstTileOfNextMip, spanningHeap));

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
            Assert.Equal(
                (ulong)packedTileCount,
                D3D12PrivateState.CountSparseMappingTiles(texture, packedAlias, packedHeap));

            Wait(backend, backend.UpdateSparseMappings(
                queue,
                [new SparseMappingDesc(texture, packedAlias, SparseMappingType.Unmapped, null, 0)]));
            Assert.Equal(
                (ulong)packedTileCount,
                D3D12PrivateState.CountSparseMappingTiles(texture, packed, null));
        }
        else
        {
            Assert.Equal(0U, info.PackedMips.PackedMipTileCount);
            Assert.Equal(0U, info.PackedMips.PackedMipTileOffset);
        }
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Automatic_generation_tracks_ordered_overwrite_and_unmap_intervals_exactly()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SparseResources? capability));
        Assert.NotNull(capability);

        ulong tileSize = capability.TileSizeInBytes;
        using Buffer resource = backend.CreateReservedBuffer(
            device,
            new BufferDesc(
                checked(tileSize * 3),
                BufferUsages.CopySource | BufferUsages.CopyDestination));
        using Heap first = CreateSparseHeap(backend, device, checked(tileSize * 3));
        using Heap second = CreateSparseHeap(backend, device, tileSize);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        SparseTileRegion whole = LinearRegion(0, 3);
        SparseTileRegion middle = LinearRegion(1, 1);

        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(resource, whole, SparseMappingType.Mapped, first, 0)]));
        Assert.Equal(3UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, first));
        Assert.Equal(0UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, second));
        Assert.Equal(0UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, null));

        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(resource, middle, SparseMappingType.Mapped, second, 0)]));
        Assert.Equal(2UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, first));
        Assert.Equal(1UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, second));
        Assert.Equal(0UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, null));

        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [
                new SparseMappingDesc(
                    resource,
                    LinearRegion(0, 1),
                    SparseMappingType.Unmapped,
                    null,
                    0),
                new SparseMappingDesc(
                    resource,
                    LinearRegion(2, 1),
                    SparseMappingType.Unmapped,
                    null,
                    0),
            ]));
        Assert.Equal(0UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, first));
        Assert.Equal(1UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, second));
        Assert.Equal(2UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, null));

        Wait(backend, backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(resource, middle, SparseMappingType.Unmapped, null, 0)]));
        Assert.Equal(0UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, first));
        Assert.Equal(0UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, second));
        Assert.Equal(3UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, null));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Copy_generation_observes_prior_entries_and_self_overlap_uses_a_snapshot()
    {
        using var backend = new D3D12Backend();
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

        AssertCopiedSparseLayout(backend, middle, whole, first, second, overwritten);
        AssertCopiedSparseLayout(backend, destination, whole, first, second, overwritten);

        SparseTileRegion twoTiles = LinearRegion(0, 2);
        Wait(backend, backend.CopySparseMappings(
            queue,
            [new SparseMappingCopyDesc(
                destination,
                new SparseTileCoordinate(1, 0, 0, 0),
                destination,
                origin,
                twoTiles)]));

        Assert.Equal(2UL, D3D12PrivateState.CountSparseMappingTiles(destination, whole, first));
        Assert.Equal(0UL, D3D12PrivateState.CountSparseMappingTiles(destination, whole, second));
        Assert.Equal(0UL, D3D12PrivateState.CountSparseMappingTiles(destination, whole, overwritten));
        Assert.Equal(1UL, D3D12PrivateState.CountSparseMappingTiles(destination, whole, null));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Submitted_sparse_generation_retains_its_heap_after_a_later_unmap()
    {
        using var backend = new D3D12Backend();
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
        Assert.Equal(1UL, D3D12PrivateState.CountSparseMappingTiles(resource, oneTile, null));
        heap.Dispose();
        Assert.NotEqual(0, D3D12PrivateState.NativeHeapPointer(heap));

        Wait(backend, unmapped);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(submitted, TimeSpan.Zero));
        backend.CollectCompleted(device);
        Assert.Equal(0, D3D12PrivateState.NativeHeapPointer(heap));
    }

    [Fact]
    public void Concurrent_queues_use_independent_sparse_transaction_workspaces()
    {
        using var backend = new D3D12Backend();
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
        Assert.Equal(
            1UL,
            D3D12PrivateState.CountSparseMappingTiles(firstResource, tile, firstHeap));
        Assert.Equal(
            1UL,
            D3D12PrivateState.CountSparseMappingTiles(secondResource, tile, secondHeap));
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
    public void Preaccept_sparse_validation_failure_rolls_back_prepared_prefix_and_retry_succeeds()
    {
        using var backend = new D3D12Backend();
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
        Assert.Equal(1UL, D3D12PrivateState.CountSparseMappingTiles(resource, first, null));
        Assert.Equal(1UL, D3D12PrivateState.CountSparseMappingTiles(resource, second, null));
        nint pointer = D3D12PrivateState.NativeHeapPointer(heap);
        Assert.NotEqual(0, pointer);

        mappings[1] = new(resource, second, SparseMappingType.Mapped, heap, 1);
        Wait(backend, backend.UpdateSparseMappings(queue, mappings));
        Assert.Equal(2UL, D3D12PrivateState.CountSparseMappingTiles(
            resource,
            LinearRegion(0, 2),
            heap));
        heap.Dispose();
        Assert.NotEqual(0, D3D12PrivateState.NativeHeapPointer(heap));
        backend.CollectCompleted(device);
    }

    private static Buffer CreateSparseBuffer(
        D3D12Backend backend,
        Device device,
        ulong tileSize,
        ulong tileCount) =>
        backend.CreateReservedBuffer(
            device,
            new BufferDesc(
                checked(tileSize * tileCount),
                BufferUsages.CopySource | BufferUsages.CopyDestination));

    private static Heap CreateSparseHeap(
        D3D12Backend backend,
        Device device,
        ulong size) =>
        backend.CreateHeap(
            device,
            new HeapDesc(size, 64 * 1024, MemoryType.DeviceLocal, HeapFlags.Buffers));

    private static Heap CreateSparseTextureHeap(
        D3D12Backend backend,
        Device device,
        ulong size) =>
        backend.CreateHeap(
            device,
            new HeapDesc(size, 64 * 1024, MemoryType.DeviceLocal, HeapFlags.Textures));

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((uint)(((ulong)value + divisor - 1) / divisor));

    private static SparseTileRegion LinearRegion(uint start, uint tileCount) =>
        new(new SparseTileCoordinate(start, 0, 0, 0), 0, 0, 0, tileCount, Boxed: false);

    private static void AssertCopiedSparseLayout(
        D3D12Backend backend,
        Buffer resource,
        in SparseTileRegion whole,
        Heap first,
        Heap second,
        Heap overwritten)
    {
        Assert.Equal(1UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, first));
        Assert.Equal(1UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, second));
        Assert.Equal(0UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, overwritten));
        Assert.Equal(1UL, D3D12PrivateState.CountSparseMappingTiles(resource, whole, null));
    }

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

    private static (uint Segment, ulong Start, ulong TileCount)[] NormalizeSparseRegion(
        in SparseResourceInfo info,
        NativeSubresourceTiling[] tilings,
        uint subresourceCount,
        in SparseTileRegion region)
    {
        const BindingFlags flags = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;
        Type stateType = typeof(D3D12Backend).GetNestedType(
            "D3D12SparseState",
            BindingFlags.NonPublic)!;
        ConstructorInfo constructor = stateType.GetConstructors(flags)
            .Single(candidate => candidate.GetParameters().Length == 3);
        object state = constructor.Invoke([info, tilings, subresourceCount]);
        try
        {
            object logicalRegion;
            try
            {
                logicalRegion = stateType.GetMethod("PrepareRegion", flags)!
                    .Invoke(state, [region])!;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(exception.InnerException)
                    .Throw();
                throw;
            }

            object enumerator = logicalRegion.GetType().GetMethod("GetEnumerator", flags)!
                .Invoke(logicalRegion, null)!;
            MethodInfo moveNext = enumerator.GetType().GetMethod("MoveNext", flags)!;
            PropertyInfo currentProperty = enumerator.GetType().GetProperty("Current", flags)!;
            var result = new List<(uint, ulong, ulong)>();
            while ((bool)moveNext.Invoke(enumerator, null)!)
            {
                object current = currentProperty.GetValue(enumerator)!;
                Type intervalType = current.GetType();
                result.Add((
                    (uint)intervalType.GetProperty("Segment", flags)!.GetValue(current)!,
                    (ulong)intervalType.GetProperty("Start", flags)!.GetValue(current)!,
                    (ulong)intervalType.GetProperty("TileCount", flags)!.GetValue(current)!));
            }
            return [.. result];
        }
        finally
        {
            ((IDisposable)state).Dispose();
        }
    }
}
