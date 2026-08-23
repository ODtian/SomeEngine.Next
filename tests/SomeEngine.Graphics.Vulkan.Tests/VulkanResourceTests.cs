namespace SomeEngine.Graphics.Vulkan.Tests;

using System.Numerics;
using Xunit;
using Xunit.Sdk;

public sealed class VulkanResourceTests
{
    [Fact]
    public void Device_creates_maps_places_and_views_core_resources()
    {
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));

        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(4096, BufferUsages.CopySource),
            MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            mapping.Bytes.Fill(0x5A);
            mapping.Flush(mapping.Range);
        }

        BufferDesc placedDesc = new(
            64 * 1024,
            BufferUsages.CopySource |
            BufferUsages.CopyDestination |
            BufferUsages.ShaderRead |
            BufferUsages.ShaderWrite);
        MemoryRequirements bufferRequirements = backend.GetBufferMemoryRequirements(
            device,
            placedDesc);
        using Heap heap = backend.CreateHeap(device, new HeapDesc(
            Math.Max(bufferRequirements.Size, 2UL * 1024 * 1024),
            bufferRequirements.Alignment,
            MemoryType.DeviceLocal,
            HeapFlags.Buffers));
        using Buffer placed = backend.CreatePlacedBuffer(device, heap, 0, placedDesc);
        using BufferSrv bufferSrv = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(placed, BufferRange.Whole, StructureStride: 16));
        using BufferUav bufferUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(placed, BufferRange.Whole, StructureStride: 16));

        TextureDesc textureDesc = new(
            TextureDimension.Texture2D,
            64,
            64,
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.CopySource |
            TextureUsages.CopyDestination |
            TextureUsages.Sampled |
            TextureUsages.Storage |
            TextureUsages.ColorAttachment);
        using Texture texture = backend.CreateTexture(device, textureDesc);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv srv = backend.CreateTextureSrv(device, new TextureSrvDesc(
            texture,
            range,
            Format.R8G8B8A8UNorm,
            TextureViewDimension.Texture2D));
        using TextureUav uav = backend.CreateTextureUav(device, new TextureUavDesc(
            texture,
            range,
            Format.R8G8B8A8UNorm,
            TextureViewDimension.Texture2D));
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                texture,
                range,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        using Sampler sampler = backend.CreateSampler(device, new SamplerDesc(
            FilterType.Linear,
            FilterType.Linear,
            FilterType.Linear,
            AddressType.Repeat,
            AddressType.Repeat,
            AddressType.Repeat));

        Assert.Equal(MemoryType.Upload, upload.Info.MemoryType);
        Assert.Same(heap, placed.Heap);
        Assert.Same(texture, srv.Resource);
        Assert.Same(texture, uav.Resource);
        Assert.Same(texture, attachment.Resource);
        Assert.Equal(FilterType.Linear, sampler.Description.MinFilter);
    }

    [Fact]
    public void Custom_border_color_extension_creates_sampler()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        SamplerDesc description = new(
            FilterType.Linear,
            FilterType.Linear,
            FilterType.Linear,
            AddressType.ClampToBorder,
            AddressType.ClampToBorder,
            AddressType.ClampToBorder,
            BorderColor: new Vector4(0.125f, 0.25f, 0.5f, 0.75f));
        try
        {
            using Sampler sampler = backend.CreateSampler(device, description);
            Assert.Equal(description.BorderColor, sampler.Description.BorderColor);
        }
        catch (NotSupportedException)
        {
            throw SkipException.ForSkip(
                "The Vulkan adapter does not expose formatless custom border colors.");
        }
    }
}
