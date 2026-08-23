namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;

public sealed class VulkanSparseTests
{
    [Fact]
    public void Sparse_buffer_bind_and_mapping_copy_alias_heap_tiles()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.SparseResources));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        BufferDesc description = new(
            128 * 1024,
            BufferUsages.CopySource | BufferUsages.CopyDestination);
        using Buffer source = backend.CreateReservedBuffer(device, description);
        using Buffer destination = backend.CreateReservedBuffer(device, description);
        SparseResourceInfo info = backend.GetSparseResourceInfo(source);
        Assert.True(info.Alignment > 0);
        using Heap heap = backend.CreateHeap(device, new HeapDesc(
            info.Alignment * 2,
            info.Alignment,
            MemoryType.DeviceLocal,
            HeapFlags.Buffers));
        SparseTileCoordinate origin = new(0, 0, 0, 0);
        SparseTileRegion oneTile = new(origin, 0, 0, 0, 1, Boxed: false);
        QueueCompletion mapped = backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(source, oneTile, SparseMappingType.Mapped, heap, 0)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(mapped, TimeSpan.FromSeconds(2)));
        QueueCompletion copied = backend.CopySparseMappings(
            queue,
            [new SparseMappingCopyDesc(destination, origin, source, origin, oneTile)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(copied, TimeSpan.FromSeconds(2)));

        int byteCount = checked((int)info.Alignment);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc((ulong)byteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc((ulong)byteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using (MappedBuffer bytes = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            bytes.Bytes.Fill(0x4D);
            bytes.Flush(bytes.Range);
        }
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, source, 0, (ulong)byteCount));
        backend.Barrier(context, new MemoryBarrier(
            PipelineSync.Copy,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(destination, 0, readback, 0, (ulong)byteCount));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion complete = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(complete, TimeSpan.FromSeconds(2)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        Assert.True(result.Bytes.ToArray().All(static value => value == 0x4D));
    }

    [Fact]
    public void Sparse_texture_reports_native_tile_and_mip_tail_shape()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.SparseResources));
        using Texture texture = backend.CreateReservedTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                1024,
                1024,
                1,
                11,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled | TextureUsages.CopyDestination));
        SparseResourceInfo info = backend.GetSparseResourceInfo(texture);
        Assert.True(info.TileShape.Width > 0);
        Assert.True(info.TileShape.Height > 0);
        Assert.True(info.TotalTileCount > 0);
        Assert.True(info.PackedMips.StandardMipLevelCount <= 11);
    }
}
