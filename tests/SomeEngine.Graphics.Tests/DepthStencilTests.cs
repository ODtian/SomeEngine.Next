using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class DepthStencilTests
{
    [Fact]
    public void D24_depth_and_stencil_planes_keep_independent_state_and_storage()
    {
        const int width = 4;
        const int height = 4;
        using Device device = new();
        TextureDesc textureDesc = new(
            width,
            height,
            Format.D24UNormS8UInt,
            TextureUsage.CopySource | TextureUsage.CopyDestination | TextureUsage.DepthStencilAttachment);
        TextureHandle texture = device.CreateTexture(textureDesc);
        TextureViewHandle view = device.CreateTextureView(new TextureViewDesc(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth | TextureAspect.Stencil),
            TextureViewUsage.DepthStencilAttachment));

        TextureCopyRegion depthRegion = new(0, 0, TextureAspect.Depth, width, height);
        TextureCopyRegion stencilRegion = new(0, 0, TextureAspect.Stencil, width, height);
        TextureCopyFootprint depthFootprint = device.GetTextureCopyFootprint(textureDesc, depthRegion);
        TextureCopyFootprint stencilFootprint = device.GetTextureCopyFootprint(textureDesc, stencilRegion);
        BufferHandle depthUpload = device.CreateBuffer(
            new BufferDesc(depthFootprint.RequiredBufferSize, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle stencilUpload = device.CreateBuffer(
            new BufferDesc(stencilFootprint.RequiredBufferSize, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle depthReadback = device.CreateBuffer(
            new BufferDesc(depthFootprint.RequiredBufferSize, BufferUsage.CopyDestination),
            MemoryType.Readback);
        BufferHandle stencilReadback = device.CreateBuffer(
            new BufferDesc(stencilFootprint.RequiredBufferSize, BufferUsage.CopyDestination),
            MemoryType.Readback);

        byte[] originalDepth = Enumerable.Repeat((byte)0x3c, checked((int)depthFootprint.FootprintSize)).ToArray();
        byte[] originalStencil = Enumerable.Repeat((byte)0xa7, checked((int)stencilFootprint.FootprintSize)).ToArray();
        device.WriteBuffer(depthUpload, depthFootprint.Layout.Offset, originalDepth);
        device.WriteBuffer(stencilUpload, stencilFootprint.Layout.Offset, originalStencil);

        TextureSubresourceRange depthRange = new(0, 1, 0, 1, TextureAspect.Depth);
        TextureSubresourceRange stencilRange = new(0, 1, 0, 1, TextureAspect.Stencil);
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics))
        {
            commands.Barriers([
                ResourceBarrier.Transition(texture.Resource, ResourceState.Common, ResourceState.CopyDestination, depthRange),
                ResourceBarrier.Transition(texture.Resource, ResourceState.Common, ResourceState.CopyDestination, stencilRange),
            ]);
            commands.CopyBufferToTexture(new BufferTextureCopy(
                depthUpload,
                depthFootprint.Layout,
                texture,
                depthRegion));
            commands.CopyBufferToTexture(new BufferTextureCopy(
                stencilUpload,
                stencilFootprint.Layout,
                texture,
                stencilRegion));
            commands.Barriers([
                ResourceBarrier.Transition(texture.Resource, ResourceState.CopyDestination, ResourceState.DepthWrite, depthRange),
                ResourceBarrier.Transition(texture.Resource, ResourceState.CopyDestination, ResourceState.DepthRead, stencilRange),
            ]);
            commands.BeginRendering(new RenderingInfo(
                ReadOnlyMemory<ColorAttachment>.Empty,
                new DepthStencilAttachment(
                    view,
                    new DepthAttachmentOperations(LoadAction.Clear, StoreAction.Store, ClearValue: 0.25f),
                    new StencilAttachmentOperations(LoadAction.Load, StoreAction.Store, ReadOnly: true)),
                width,
                height));
            commands.EndRendering();
            commands.Barriers([
                ResourceBarrier.Transition(texture.Resource, ResourceState.DepthWrite, ResourceState.CopySource, depthRange),
                ResourceBarrier.Transition(texture.Resource, ResourceState.DepthRead, ResourceState.CopySource, stencilRange),
            ]);
            commands.CopyTextureToBuffer(new TextureBufferCopy(
                texture,
                depthRegion,
                depthReadback,
                depthFootprint.Layout));
            commands.CopyTextureToBuffer(new TextureBufferCopy(
                texture,
                stencilRegion,
                stencilReadback,
                stencilFootprint.Layout));

            GpuCompletion completion = device.Submit(QueueType.Graphics, [commands.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(1)));
        }

        byte[] actualDepth = new byte[originalDepth.Length];
        byte[] actualStencil = new byte[originalStencil.Length];
        device.ReadBuffer(depthReadback, depthFootprint.Layout.Offset, actualDepth);
        device.ReadBuffer(stencilReadback, stencilFootprint.Layout.Offset, actualStencil);
        Assert.All(actualDepth, static value => Assert.Equal(0, value));
        Assert.Equal(originalStencil, actualStencil);

        device.DestroyBuffer(stencilReadback);
        device.DestroyBuffer(depthReadback);
        device.DestroyBuffer(stencilUpload);
        device.DestroyBuffer(depthUpload);
        device.DestroyTextureView(view);
        device.DestroyTexture(texture);
    }

    [Fact]
    public void Read_only_attachment_planes_require_load_and_store()
    {
        using Device device = new();
        TextureHandle texture = device.CreateTexture(new TextureDesc(
            4,
            4,
            Format.D24UNormS8UInt,
            TextureUsage.DepthStencilAttachment));
        TextureViewHandle view = device.CreateTextureView(new TextureViewDesc(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth | TextureAspect.Stencil),
            TextureViewUsage.DepthStencilAttachment));
        using ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics);

        ArgumentException depthError = Assert.Throws<ArgumentException>(() => commands.BeginRendering(new RenderingInfo(
            ReadOnlyMemory<ColorAttachment>.Empty,
            new DepthStencilAttachment(
                view,
                new DepthAttachmentOperations(LoadAction.Clear, StoreAction.Store, ReadOnly: true)),
            4,
            4)));
        Assert.Contains("Read-only attachment planes require Load/Store", depthError.Message, StringComparison.Ordinal);

        ArgumentException stencilError = Assert.Throws<ArgumentException>(() => commands.BeginRendering(new RenderingInfo(
            ReadOnlyMemory<ColorAttachment>.Empty,
            new DepthStencilAttachment(
                view,
                null,
                new StencilAttachmentOperations(LoadAction.Load, StoreAction.Discard, ReadOnly: true)),
            4,
            4)));
        Assert.Contains("Read-only attachment planes require Load/Store", stencilError.Message, StringComparison.Ordinal);

        device.DestroyTextureView(view);
        device.DestroyTexture(texture);
    }
}
