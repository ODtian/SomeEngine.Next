using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class ResourceMetadataTests
{
    [Fact]
    public void Warp_reports_committed_and_shared_placed_allocation_metadata()
    {

        using Device device = new(new Options { UseWarpAdapter = true });
        BufferDesc desc = new(256, BufferUsage.CopySource);
        BufferHandle committed = device.CreateBuffer(desc, MemoryType.Upload);
        BufferMetadata committedMetadata = device.GetBufferMetadata(committed);
        ResourceRequirements requirements = device.GetBufferRequirements(desc, MemoryType.DeviceLocal);
        HeapHandle heap = device.CreateHeap(new HeapDesc(
            checked(requirements.Size * 2),
            MemoryType.DeviceLocal,
            ResourceHeapClass.Buffer));
        BufferHandle first = device.CreatePlacedBuffer(heap, 0, desc);
        BufferHandle second = device.CreatePlacedBuffer(heap, requirements.Size, desc);
        BufferMetadata firstMetadata = device.GetBufferMetadata(first);
        BufferMetadata secondMetadata = device.GetBufferMetadata(second);

        Assert.Equal(desc, committedMetadata.Description);
        Assert.Equal(MemoryType.Upload, committedMetadata.MemoryType);
        Assert.NotEqual(committedMetadata.Allocation.Identity, firstMetadata.Allocation.Identity);
        Assert.Equal(firstMetadata.Allocation.Identity, secondMetadata.Allocation.Identity);
        Assert.Equal(requirements.Size, firstMetadata.Allocation.Size);
        Assert.Equal(requirements.Size, secondMetadata.Allocation.Offset);

        device.DestroyBuffer(second);
        device.DestroyBuffer(first);
        device.DestroyHeap(heap);
        device.DestroyBuffer(committed);
        device.CollectGarbage();
    }

    [Fact]
    public void Warp_supports_d24_and_cpu_visible_placed_buffer_round_trip()
    {

        using Device device = new(new Options { UseWarpAdapter = true });
        TextureDesc d24 = new(
            16,
            16,
            Format.D24UNormS8UInt,
            TextureUsage.DepthStencilAttachment);
        ResourceRequirements d24Requirements = device.GetTextureRequirements(d24);
        Assert.True(d24Requirements.Size > 0);
        TextureHandle d24Texture = device.CreateTexture(d24);
        TextureViewHandle d24View = device.CreateTextureView(new TextureViewDesc(
            d24Texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth | TextureAspect.Stencil),
            TextureViewUsage.DepthStencilAttachment));
        Assert.Throws<NotSupportedException>(() => device.GetTextureCopyFootprint(
            d24,
            new TextureCopyRegion(0, 0, TextureAspect.Depth, 1, 1)));
        device.DestroyTextureView(d24View);
        device.DestroyTexture(d24Texture);

        BufferDesc uploadDesc = new(256, BufferUsage.CopySource | BufferUsage.ShaderRead);
        ResourceRequirements uploadRequirements = device.GetBufferRequirements(uploadDesc, MemoryType.Upload);
        HeapHandle uploadHeap = device.CreateHeap(new HeapDesc(
            uploadRequirements.Size,
            MemoryType.Upload,
            ResourceHeapClass.Buffer));
        BufferHandle upload = device.CreatePlacedBuffer(uploadHeap, 0, uploadDesc);
        BufferViewHandle uploadView = device.CreateBufferView(new BufferViewDesc(
            upload,
            new BufferRange(0, 256),
            BindingKind.ReadOnlyBuffer,
            Stride: 4));
        byte[] expected = Enumerable.Range(0, 256).Select(static value => checked((byte)value)).ToArray();
        device.WriteBuffer(upload, 0, expected);

        BufferDesc readbackDesc = new(256, BufferUsage.CopyDestination);
        ResourceRequirements readbackRequirements = device.GetBufferRequirements(readbackDesc, MemoryType.Readback);
        HeapHandle readbackHeap = device.CreateHeap(new HeapDesc(
            readbackRequirements.Size,
            MemoryType.Readback,
            ResourceHeapClass.Buffer));
        BufferHandle readback = device.CreatePlacedBuffer(readbackHeap, 0, readbackDesc);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.CopyBuffer(upload, 0, readback, 0, 256);
            GpuCompletion completion = device.Submit(QueueType.Copy, [commands.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(5)));
        }

        byte[] actual = new byte[256];
        device.ReadBuffer(readback, 0, actual);
        Assert.Equal(expected, actual);
        Assert.Equal(MemoryType.Upload, device.GetBufferMetadata(upload).MemoryType);
        Assert.Equal(MemoryType.Readback, device.GetBufferMetadata(readback).MemoryType);
        Assert.Throws<ArgumentException>(() => device.GetBufferRequirements(
            new BufferDesc(256, BufferUsage.ShaderWrite),
            MemoryType.Upload));
        Assert.Throws<ArgumentException>(() => device.CreateHeap(new HeapDesc(
            65_536,
            MemoryType.Readback,
            ResourceHeapClass.Texture)));

        device.DestroyBufferView(uploadView);
        device.DestroyBuffer(readback);
        device.DestroyHeap(readbackHeap);
        device.DestroyBuffer(upload);
        device.DestroyHeap(uploadHeap);
        device.CollectGarbage();
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
    }
}
