using System.Reflection;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;
using NativeBarrierAccess = Silk.NET.Direct3D12.BarrierAccess;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpAliasingBarrierTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 1, true)]
    [InlineData(1, 2, true)]
    [InlineData(2, 2, true)]
    public void Legacy_aliasing_cardinality_selects_exact_or_global_encoding(
        int beforeCount,
        int afterCount,
        bool global)
    {
        Assert.Equal(
            global,
            D3D12Backend.RequiresGlobalLegacyAliasingBarrier(beforeCount, afterCount));
    }

    [Fact]
    public void Enhanced_aliasing_state_orders_both_halves_and_leaves_after_uninitialized()
    {
        D3D12Backend.EnhancedAliasingBarrierState before =
            D3D12Backend.GetEnhancedAliasingBarrierState(activate: false);
        D3D12Backend.EnhancedAliasingBarrierState after =
            D3D12Backend.GetEnhancedAliasingBarrierState(activate: true);

        Assert.Equal(PipelineSync.All, before.SyncBefore);
        Assert.Equal(PipelineSync.All, before.SyncAfter);
        Assert.Equal(NativeBarrierAccess.Common, before.AccessBefore);
        Assert.Equal(NativeBarrierAccess.NoAccess, before.AccessAfter);
        Assert.False(before.Discard);
        Assert.Equal(PipelineSync.All, after.SyncBefore);
        Assert.Equal(PipelineSync.All, after.SyncAfter);
        Assert.Equal(NativeBarrierAccess.NoAccess, after.AccessBefore);
        Assert.Equal(NativeBarrierAccess.NoAccess, after.AccessAfter);
        Assert.True(after.Discard);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overlapping_placed_buffers_execute_two_to_one_and_one_to_two_aliasing(
        bool validation)
    {
        const ulong halfSize = 65_536;
        const ulong fullSize = halfSize * 2;
        using IGraphicsBackend backend = CreateBackend(validation);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(UsesEnhancedBarriers(device));
        using Heap heap = backend.CreateHeap(
            device,
            new HeapDesc(fullSize, 0, MemoryType.DeviceLocal, HeapFlags.Buffers));
        BufferDesc halfDesc = new(
            halfSize,
            BufferUsages.CopySource | BufferUsages.CopyDestination);
        BufferDesc fullDesc = new(
            fullSize,
            BufferUsages.CopySource | BufferUsages.CopyDestination);
        using Buffer before0 = backend.CreatePlacedBuffer(device, heap, 0, halfDesc);
        using Buffer before1 = backend.CreatePlacedBuffer(device, heap, halfSize, halfDesc);
        using Buffer combined = backend.CreatePlacedBuffer(device, heap, 0, fullDesc);
        using Buffer after0 = backend.CreatePlacedBuffer(device, heap, 0, halfDesc);
        using Buffer after1 = backend.CreatePlacedBuffer(device, heap, halfSize, halfDesc);
        using Buffer precedingUpload = CreateUpload(backend, device, fullSize, 17);
        using Buffer combinedUpload = CreateUpload(backend, device, fullSize, 53);
        using Buffer splitUpload = CreateUpload(backend, device, fullSize, 91);
        using Buffer readbackA = backend.CreateBuffer(
            device,
            new BufferDesc(fullSize, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using Buffer readbackB = backend.CreateBuffer(
            device,
            new BufferDesc(fullSize, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        Transition(backend, context, before0, ResourceAccess.NoAccess, ResourceAccess.CopyDestination);
        Transition(backend, context, before1, ResourceAccess.NoAccess, ResourceAccess.CopyDestination);
        backend.CopyBuffer(context, new BufferCopy(precedingUpload, 0, before0, 0, halfSize));
        backend.CopyBuffer(context, new BufferCopy(precedingUpload, halfSize, before1, 0, halfSize));
        backend.Barrier(
            context,
            new AliasingBarrier(
                [new AliasingResource(before0), new AliasingResource(before1)],
                [new AliasingResource(combined)]));
        Transition(backend, context, combined, ResourceAccess.NoAccess, ResourceAccess.CopyDestination);
        backend.CopyBuffer(context, new BufferCopy(combinedUpload, 0, combined, 0, fullSize));
        Transition(backend, context, combined, ResourceAccess.CopyDestination, ResourceAccess.CopySource);
        backend.CopyBuffer(context, new BufferCopy(combined, 0, readbackA, 0, fullSize));
        backend.Barrier(
            context,
            new AliasingBarrier(
                [new AliasingResource(combined)],
                [new AliasingResource(after0), new AliasingResource(after1)]));
        Transition(backend, context, after0, ResourceAccess.NoAccess, ResourceAccess.CopyDestination);
        Transition(backend, context, after1, ResourceAccess.NoAccess, ResourceAccess.CopyDestination);
        backend.CopyBuffer(context, new BufferCopy(splitUpload, 0, after0, 0, halfSize));
        backend.CopyBuffer(context, new BufferCopy(splitUpload, halfSize, after1, 0, halfSize));
        Transition(backend, context, after0, ResourceAccess.CopyDestination, ResourceAccess.CopySource);
        Transition(backend, context, after1, ResourceAccess.CopyDestination, ResourceAccess.CopySource);
        backend.CopyBuffer(context, new BufferCopy(after0, 0, readbackB, 0, halfSize));
        backend.CopyBuffer(context, new BufferCopy(after1, 0, readbackB, halfSize, halfSize));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = Submit(
            backend,
            backend.GetQueue(device, QueueType.Graphics),
            commands);
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));

        AssertPattern(backend, readbackA, fullSize, 53);
        AssertPattern(backend, readbackB, fullSize, 91);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Aliasing_barrier_validates_all_entries_before_mutation_and_allows_legal_retry(
        bool validation)
    {
        using IGraphicsBackend backend = CreateBackend(validation);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.ShaderRead),
            MemoryType.DeviceLocal);
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                4,
                4,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled));
        TextureSubresourceRange legal = new(0, 1, 0, 1, TextureAspects.Color);
        TextureSubresourceRange invalid = new(0, 2, 0, 1, TextureAspects.Color);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        AssertInvalid(
            () => backend.Barrier(context, new AliasingBarrier([], [])),
            validation);
        AssertInvalid(() =>
            backend.Barrier(
                context,
                new AliasingBarrier(
                    [new AliasingResource(buffer)],
                    [new AliasingResource(texture, invalid)])),
            validation);
        AssertInvalid(() =>
            backend.Barrier(
                context,
                new AliasingBarrier(
                    [new AliasingResource(buffer, legal)],
                    [new AliasingResource(texture, legal)])),
            validation);
        if (validation)
        {
            using Device foreignDevice = D3D12TestSupport.CreateWarpDevice(backend);
            using Buffer foreignBuffer = backend.CreateBuffer(
                foreignDevice,
                new BufferDesc(256, BufferUsages.ShaderRead),
                MemoryType.DeviceLocal);
            Assert.Throws<InvalidOperationException>(() =>
                backend.Barrier(
                    context,
                    new AliasingBarrier(
                        [new AliasingResource(buffer)],
                        [new AliasingResource(foreignBuffer)])));
        }
        backend.Barrier(
            context,
            new AliasingBarrier(
                [new AliasingResource(buffer)],
                [new AliasingResource(texture, legal)]));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = Submit(
            backend,
            backend.GetQueue(device, QueueType.Graphics),
            commands);

        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Aliasing_barrier_preserves_cardinality_resource_kinds_and_before_after_ordinal(
        bool validation)
    {
        using IGraphicsBackend backend = CreateBackend(validation);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out RayTracing? rayTracing));
        Assert.NotNull(rayTracing);

        using Buffer first = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.ShaderRead),
            MemoryType.DeviceLocal);
        using Buffer second = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.ShaderRead),
            MemoryType.DeviceLocal);
        using Texture firstTexture = CreateTexture(backend, device);
        using Texture secondTexture = CreateTexture(backend, device);
        using Buffer firstStorage = CreateAccelerationStructureStorage(backend, device);
        using Buffer secondStorage = CreateAccelerationStructureStorage(backend, device);
        using AccelerationStructure firstStructure = backend.CreateAccelerationStructure(
            device,
            firstStorage,
            BufferRange.Whole,
            AccelerationStructureType.BottomLevel);
        using AccelerationStructure secondStructure = backend.CreateAccelerationStructure(
            device,
            secondStorage,
            BufferRange.Whole,
            AccelerationStructureType.BottomLevel);
        TextureSubresourceRange textureRange =
            new(0, 1, 0, 1, TextureAspects.Color);
        AliasingResource firstBuffer = new(first);
        AliasingResource secondBuffer = new(second);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        backend.Barrier(context, new AliasingBarrier([], [firstBuffer]));
        backend.Barrier(context, new AliasingBarrier([firstBuffer], []));
        backend.Barrier(context, new AliasingBarrier([secondBuffer], [firstBuffer]));
        backend.Barrier(context, new AliasingBarrier([], [secondBuffer]));
        backend.Barrier(
            context,
            new AliasingBarrier([firstBuffer, secondBuffer], [firstBuffer]));
        backend.Barrier(
            context,
            new AliasingBarrier([firstBuffer], [firstBuffer, secondBuffer]));
        backend.Barrier(
            context,
            new AliasingBarrier(
                [firstBuffer, secondBuffer],
                [firstBuffer, secondBuffer]));
        backend.Barrier(
            context,
            new AliasingBarrier(
                [new AliasingResource(firstTexture, textureRange)],
                [new AliasingResource(secondTexture)]));
        AssertInvalid(() =>
            backend.Barrier(
                context,
                new AliasingBarrier(
                    [new AliasingResource(firstStructure, textureRange)],
                    [new AliasingResource(secondStructure)])),
            validation);
        backend.Barrier(
            context,
            new AliasingBarrier(
                [new AliasingResource(firstStructure)],
                [new AliasingResource(secondStructure)]));

        // The same logical resource is deactivated before it is activated. The
        // Validation mirror must therefore leave it available for this transition.
        backend.Barrier(context, new AliasingBarrier([firstBuffer], [firstBuffer]));
        backend.Barrier(
            context,
            new BufferBarrier(
                first,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopySource));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = Submit(
            backend,
            backend.GetQueue(device, QueueType.Graphics),
            commands);

        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Validation_rejects_acceleration_structure_texture_range_before_legal_retry()
    {
        using IGraphicsBackend backend = CreateBackend(validation: true);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out RayTracing? rayTracing));
        Assert.NotNull(rayTracing);
        using Buffer storage = CreateAccelerationStructureStorage(backend, device);
        using AccelerationStructure structure = backend.CreateAccelerationStructure(
            device,
            storage,
            BufferRange.Whole,
            AccelerationStructureType.BottomLevel);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        TextureSubresourceRange textureRange =
            new(0, 1, 0, 1, TextureAspects.Color);

        backend.Begin(context);
        Transition(
            backend,
            context,
            storage,
            ResourceAccess.NoAccess,
            ResourceAccess.CopySource);
        Assert.Throws<InvalidOperationException>(() =>
            backend.Barrier(
                context,
                new AliasingBarrier(
                    [new AliasingResource(structure, textureRange)],
                    [])));
        backend.Barrier(
            context,
            new AliasingBarrier([], [new AliasingResource(structure)]));
        Transition(
            backend,
            context,
            storage,
            ResourceAccess.CopySource,
            ResourceAccess.CopyDestination);
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = Submit(
            backend,
            backend.GetQueue(device, QueueType.Graphics),
            commands);

        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
    }

    private static void AssertInvalid(Action action, bool validation)
    {
        if (validation)
            Assert.Throws<InvalidOperationException>(action);
        else
            Assert.ThrowsAny<ArgumentException>(action);
    }

    private static bool UsesEnhancedBarriers(Device device) =>
        (bool)device.GetType().GetProperty(
            "EnhancedBarriers",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(device)!;

    private static Buffer CreateUpload(
        IGraphicsBackend backend,
        Device device,
        ulong size,
        byte seed)
    {
        Buffer result = backend.CreateBuffer(
            device,
            new BufferDesc(size, BufferUsages.CopySource),
            MemoryType.Upload);
        try
        {
            using MappedBuffer mapped = backend.Map(
                result,
                MapType.Write,
                new BufferRange(0, size));
            for (int index = 0; index < mapped.Bytes.Length; index++)
                mapped.Bytes[index] = unchecked((byte)(index * 37 + seed));
            mapped.Flush(new BufferRange(0, size));
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static void AssertPattern(
        IGraphicsBackend backend,
        Buffer readback,
        ulong size,
        byte seed)
    {
        using MappedBuffer result = backend.Map(
            readback,
            MapType.Read,
            new BufferRange(0, size));
        result.Invalidate(new BufferRange(0, size));
        for (int index = 0; index < result.Bytes.Length; index++)
            Assert.Equal(unchecked((byte)(index * 37 + seed)), result.Bytes[index]);
    }

    private static void Transition(
        IGraphicsBackend backend,
        CommandContext context,
        Buffer buffer,
        ResourceAccess before,
        ResourceAccess after) =>
        backend.Barrier(
            context,
            new BufferBarrier(
                buffer,
                before == ResourceAccess.NoAccess ? PipelineSync.None : PipelineSync.Copy,
                after == ResourceAccess.NoAccess ? PipelineSync.None : PipelineSync.Copy,
                before,
                after));

    private static IGraphicsBackend CreateBackend(bool validation)
    {
        D3D12ValidationOptions options = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true);
        D3D12Backend native = new(new D3D12BackendOptions(options));
        return validation ? new ValidationLayer(native) : native;
    }

    private static Texture CreateTexture(IGraphicsBackend backend, Device device) =>
        backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                4,
                4,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled));

    private static Buffer CreateAccelerationStructureStorage(
        IGraphicsBackend backend,
        Device device) =>
        backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.AccelerationStructure),
            MemoryType.DeviceLocal);

    private static QueueCompletion Submit(
        IGraphicsBackend backend,
        Queue queue,
        RecordedCommands commands)
    {
        RecordedCommands[] batch = [commands];
        return backend.Submit(queue, new QueueSubmitDesc([], [], batch, [], []));
    }
}
