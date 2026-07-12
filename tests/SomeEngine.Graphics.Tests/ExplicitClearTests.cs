using System.Buffers.Binary;
using System.Numerics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

internal sealed class PortableClearScenarios
{
    public void Null_clears_exact_buffer_range_with_repeating_uint_pattern()
    {
        using Device device = new();
        BufferHandle buffer = device.CreateBuffer(new BufferDesc(
            16, BufferUsage.CopyDestination | BufferUsage.CopySource));
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopyDestination), MemoryType.Readback);
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.Barriers([ResourceBarrier.Transition(
                buffer.Resource, ResourceState.Common, ResourceState.CopyDestination)]);
            commands.ClearBuffer(buffer, new BufferRange(3, 7), 0x44332211);
            commands.Barriers([ResourceBarrier.Transition(
                buffer.Resource, ResourceState.CopyDestination, ResourceState.CopySource)]);
            commands.CopyBuffer(buffer, 0, readback, 0, 16);
            device.Submit(QueueType.Copy, [commands.Finish()]);
        }
        byte[] actual = new byte[16];
        device.ReadBuffer(readback, 0, actual);
        Assert.Equal(new byte[] { 0, 0, 0, 0x11, 0x22, 0x33, 0x44, 0x11, 0x22, 0x33, 0, 0, 0, 0, 0, 0 }, actual);
        device.DestroyBuffer(readback);
        device.DestroyBuffer(buffer);
    }

    public void Null_color_and_depth_stencil_clears_write_only_selected_aspects()
    {
        using Device device = new();
        TextureDesc colorDesc = new(2, 2, Format.R8G8B8A8UNorm,
            TextureUsage.ColorAttachment | TextureUsage.CopySource);
        TextureHandle color = device.CreateTexture(colorDesc);
        TextureCopyRegion colorRegion = new(0, 0, TextureAspect.Color, 2, 2);
        TextureCopyFootprint colorFootprint = device.GetTextureCopyFootprint(colorDesc, colorRegion);
        BufferHandle colorReadback = device.CreateBuffer(
            new BufferDesc(colorFootprint.RequiredBufferSize, BufferUsage.CopyDestination), MemoryType.Readback);

        TextureDesc depthDesc = new(2, 2, Format.D24UNormS8UInt,
            TextureUsage.DepthStencilAttachment | TextureUsage.CopySource);
        TextureHandle depth = device.CreateTexture(depthDesc);
        TextureCopyRegion depthRegion = new(0, 0, TextureAspect.Depth, 2, 2);
        TextureCopyRegion stencilRegion = depthRegion with { Aspect = TextureAspect.Stencil };
        TextureCopyFootprint depthFootprint = device.GetTextureCopyFootprint(depthDesc, depthRegion);
        TextureCopyFootprint stencilFootprint = device.GetTextureCopyFootprint(depthDesc, stencilRegion);
        BufferHandle depthReadback = device.CreateBuffer(
            new BufferDesc(depthFootprint.RequiredBufferSize, BufferUsage.CopyDestination), MemoryType.Readback);
        BufferHandle stencilReadback = device.CreateBuffer(
            new BufferDesc(stencilFootprint.RequiredBufferSize, BufferUsage.CopyDestination), MemoryType.Readback);
        TextureSubresourceRange depthStencilRange = new(0, 1, 0, 1, TextureAspect.Depth | TextureAspect.Stencil);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics))
        {
            commands.Barriers([
                ResourceBarrier.Transition(color.Resource, ResourceState.Common, ResourceState.RenderTarget),
                ResourceBarrier.Transition(depth.Resource, ResourceState.Common, ResourceState.DepthWrite, depthStencilRange),
            ]);
            commands.ClearTexture(color, TextureSubresourceRange.WholeColor, new Vector4(0.25f, 0.5f, 1f, 1f));
            commands.ClearDepthStencilTexture(depth, depthStencilRange, 0.5f, 0x7B);
            commands.Barriers([
                ResourceBarrier.Transition(color.Resource, ResourceState.RenderTarget, ResourceState.CopySource),
                ResourceBarrier.Transition(depth.Resource, ResourceState.DepthWrite, ResourceState.CopySource, depthStencilRange),
            ]);
            commands.CopyTextureToBuffer(new TextureBufferCopy(color, colorRegion, colorReadback, colorFootprint.Layout));
            commands.CopyTextureToBuffer(new TextureBufferCopy(depth, depthRegion, depthReadback, depthFootprint.Layout));
            commands.CopyTextureToBuffer(new TextureBufferCopy(depth, stencilRegion, stencilReadback, stencilFootprint.Layout));
            device.Submit(QueueType.Graphics, [commands.Finish()]);
        }

        byte[] colorBytes = new byte[checked((int)colorFootprint.RequiredBufferSize)];
        device.ReadBuffer(colorReadback, 0, colorBytes);
        for (int offset = 0; offset < colorBytes.Length; offset += 4)
            Assert.Equal(new byte[] { 64, 128, 255, 255 }, colorBytes.AsSpan(offset, 4).ToArray());
        byte[] depthBytes = new byte[checked((int)depthFootprint.RequiredBufferSize)];
        device.ReadBuffer(depthReadback, 0, depthBytes);
        for (int offset = 0; offset < depthBytes.Length; offset += 4)
            Assert.Equal(0x800000u, BinaryPrimitives.ReadUInt32LittleEndian(depthBytes.AsSpan(offset, 4)) & 0xFFFFFFu);
        byte[] stencilBytes = new byte[checked((int)stencilFootprint.RequiredBufferSize)];
        device.ReadBuffer(stencilReadback, 0, stencilBytes);
        Assert.All(stencilBytes, static value => Assert.Equal(0x7B, value));

        device.DestroyBuffer(stencilReadback);
        device.DestroyBuffer(depthReadback);
        device.DestroyTexture(depth);
        device.DestroyBuffer(colorReadback);
        device.DestroyTexture(color);
    }
}
