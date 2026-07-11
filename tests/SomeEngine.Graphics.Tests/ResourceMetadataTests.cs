using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class ResourceMetadataTests
{
    [Fact]
    public void Committed_and_placed_resources_report_exact_allocation_metadata()
    {
        using Device device = new();
        BufferDesc committedDesc = new(32, BufferUsage.CopySource);
        BufferHandle committed = device.CreateBuffer(committedDesc, MemoryType.Upload);
        BufferMetadata committedMetadata = device.GetBufferMetadata(committed);

        Assert.Equal(committedDesc, committedMetadata.Description);
        Assert.Equal(MemoryType.Upload, committedMetadata.MemoryType);
        Assert.Equal(0UL, committedMetadata.Allocation.Offset);
        Assert.Equal(device.GetBufferRequirements(committedDesc, MemoryType.Upload).Size, committedMetadata.Allocation.Size);

        BufferDesc placedDesc = new(16, BufferUsage.CopySource);
        ResourceRequirements requirements = device.GetBufferRequirements(placedDesc, MemoryType.DeviceLocal);
        HeapHandle heap = device.CreateHeap(new HeapDesc(
            checked(requirements.Size * 2),
            MemoryType.DeviceLocal,
            ResourceHeapClass.Buffer));
        BufferHandle first = device.CreatePlacedBuffer(heap, 0, placedDesc);
        BufferHandle second = device.CreatePlacedBuffer(heap, requirements.Size, placedDesc);
        BufferMetadata firstMetadata = device.GetBufferMetadata(first);
        BufferMetadata secondMetadata = device.GetBufferMetadata(second);

        Assert.Equal(firstMetadata.Allocation.Identity, secondMetadata.Allocation.Identity);
        Assert.NotEqual(committedMetadata.Allocation.Identity, firstMetadata.Allocation.Identity);
        Assert.Equal(0UL, firstMetadata.Allocation.Offset);
        Assert.Equal(requirements.Size, firstMetadata.Allocation.Size);
        Assert.Equal(requirements.Size, secondMetadata.Allocation.Offset);
        Assert.Equal(MemoryType.DeviceLocal, secondMetadata.MemoryType);

        device.DestroyBuffer(second);
        device.DestroyBuffer(first);
        device.DestroyHeap(heap);
        device.DestroyBuffer(committed);
    }

    [Fact]
    public void D24_and_cpu_visible_placed_buffers_are_supported_while_cpu_visible_textures_are_rejected()
    {
        using Device device = new();
        TextureDesc d24 = new(
            16,
            16,
            Format.D24UNormS8UInt,
            TextureUsage.DepthStencilAttachment);

        ResourceRequirements d24Requirements = device.GetTextureRequirements(d24);
        Assert.True(d24Requirements.Size >= 16UL * 16UL * 5UL);
        TextureHandle d24Texture = device.CreateTexture(d24);
        TextureViewHandle d24View = device.CreateTextureView(new TextureViewDesc(
            d24Texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth | TextureAspect.Stencil),
            TextureViewUsage.DepthStencilAttachment));
        device.DestroyTextureView(d24View);
        device.DestroyTexture(d24Texture);

        BufferDesc uploadDesc = new(16, BufferUsage.CopySource | BufferUsage.ShaderRead);
        ResourceRequirements uploadRequirements = device.GetBufferRequirements(uploadDesc, MemoryType.Upload);
        HeapHandle uploadBufferHeap = device.CreateHeap(new HeapDesc(
            uploadRequirements.Size,
            MemoryType.Upload,
            ResourceHeapClass.Buffer));
        BufferHandle upload = device.CreatePlacedBuffer(uploadBufferHeap, 0, uploadDesc);
        device.WriteBuffer(upload, 0, [1, 2, 3, 4]);
        BufferMetadata uploadMetadata = device.GetBufferMetadata(upload);
        Assert.Equal(MemoryType.Upload, uploadMetadata.MemoryType);
        Assert.Equal(uploadRequirements.MemoryType, uploadMetadata.MemoryType);

        BufferDesc readbackDesc = new(16, BufferUsage.CopyDestination);
        ResourceRequirements readbackRequirements = device.GetBufferRequirements(readbackDesc, MemoryType.Readback);
        HeapHandle readbackBufferHeap = device.CreateHeap(new HeapDesc(
            readbackRequirements.Size,
            MemoryType.Readback,
            ResourceHeapClass.Buffer));
        BufferHandle readback = device.CreatePlacedBuffer(readbackBufferHeap, 0, readbackDesc);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.CopyBuffer(upload, 0, readback, 0, 4);
            GpuCompletion completion = device.Submit(QueueType.Copy, [commands.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(1)));
        }
        Span<byte> bytes = stackalloc byte[4];
        device.ReadBuffer(readback, 0, bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes.ToArray());

        Assert.Throws<ArgumentException>(() => device.GetBufferRequirements(
            new BufferDesc(16, BufferUsage.ShaderWrite),
            MemoryType.Upload));
        Assert.Throws<ArgumentException>(() => device.GetBufferRequirements(
            new BufferDesc(16, BufferUsage.CopyDestination | BufferUsage.ShaderRead),
            MemoryType.Readback));

        device.DestroyBuffer(readback);
        device.DestroyHeap(readbackBufferHeap);
        device.DestroyBuffer(upload);
        device.DestroyHeap(uploadBufferHeap);

        Assert.Throws<ArgumentException>(() => device.CreateHeap(new HeapDesc(
            65_536,
            MemoryType.Readback,
            ResourceHeapClass.Texture)));
    }
}
