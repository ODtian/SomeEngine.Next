using System.Buffers.Binary;
using System.Numerics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class ResourceClearTests
{
    [Fact]
    public void Warp_clear_buffer_color_depth_and_stencil_produces_observable_results()
    {
        Assert.True(OperatingSystem.IsWindows());
        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });

        BufferHandle clearedBuffer = device.CreateBuffer(new BufferDesc(
            16, BufferUsage.CopyDestination | BufferUsage.CopySource));
        BufferHandle bufferReadback = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopyDestination), MemoryType.Readback);

        TextureDesc colorDesc = new(2, 2, Format.R8G8B8A8UNorm,
            TextureUsage.ColorAttachment | TextureUsage.CopySource);
        TextureHandle color = device.CreateTexture(colorDesc);
        TextureCopyRegion colorRegion = new(0, 0, TextureAspect.Color, 2, 2);
        TextureCopyFootprint colorFootprint = device.GetTextureCopyFootprint(colorDesc, colorRegion);
        BufferHandle colorReadback = device.CreateBuffer(
            new BufferDesc(colorFootprint.RequiredBufferSize, BufferUsage.CopyDestination), MemoryType.Readback);

        TextureDesc depthDesc = new(2, 2, Format.D24UNormS8UInt,
            TextureUsage.DepthStencilAttachment | TextureUsage.CopySource);
        TextureHandle depthStencil = device.CreateTexture(depthDesc);
        TextureCopyRegion depthRegion = new(0, 0, TextureAspect.Depth, 2, 2);
        TextureCopyRegion stencilRegion = depthRegion with { Aspect = TextureAspect.Stencil };
        TextureCopyFootprint depthFootprint = device.GetTextureCopyFootprint(depthDesc, depthRegion);
        TextureCopyFootprint stencilFootprint = device.GetTextureCopyFootprint(depthDesc, stencilRegion);
        BufferHandle depthReadback = device.CreateBuffer(
            new BufferDesc(depthFootprint.RequiredBufferSize, BufferUsage.CopyDestination), MemoryType.Readback);
        BufferHandle stencilReadback = device.CreateBuffer(
            new BufferDesc(stencilFootprint.RequiredBufferSize, BufferUsage.CopyDestination), MemoryType.Readback);
        TextureSubresourceRange depthStencilRange = new(
            0, 1, 0, 1, TextureAspect.Depth | TextureAspect.Stencil);

        GpuCompletion completion;
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics, "resource-clears"))
        {
            commands.Barriers([
                ResourceBarrier.Transition(clearedBuffer.Resource, ResourceState.Common, ResourceState.CopyDestination),
                ResourceBarrier.Transition(color.Resource, ResourceState.Common, ResourceState.RenderTarget),
                ResourceBarrier.Transition(depthStencil.Resource, ResourceState.Common, ResourceState.DepthWrite, depthStencilRange),
            ]);
            commands.ClearBuffer(clearedBuffer, new BufferRange(3, 7), 0x44332211);
            commands.ClearTexture(color, TextureSubresourceRange.WholeColor, new Vector4(0.25f, 0.5f, 1f, 1f));
            commands.ClearDepthStencilTexture(depthStencil, depthStencilRange, 0.5f, 0x7B);
            commands.Barriers([
                ResourceBarrier.Transition(clearedBuffer.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
                ResourceBarrier.Transition(color.Resource, ResourceState.RenderTarget, ResourceState.CopySource),
                ResourceBarrier.Transition(depthStencil.Resource, ResourceState.DepthWrite, ResourceState.CopySource, depthStencilRange),
            ]);
            commands.CopyBuffer(clearedBuffer, 0, bufferReadback, 0, 16);
            commands.CopyTextureToBuffer(new TextureBufferCopy(color, colorRegion, colorReadback, colorFootprint.Layout));
            commands.CopyTextureToBuffer(new TextureBufferCopy(depthStencil, depthRegion, depthReadback, depthFootprint.Layout));
            commands.CopyTextureToBuffer(new TextureBufferCopy(depthStencil, stencilRegion, stencilReadback, stencilFootprint.Layout));
            completion = device.Submit(QueueType.Graphics, [commands.Finish()]);
        }
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        byte[] bufferBytes = new byte[16];
        device.ReadBuffer(bufferReadback, 0, bufferBytes);
        Assert.Equal(new byte[] { 0, 0, 0, 0x11, 0x22, 0x33, 0x44, 0x11, 0x22, 0x33, 0, 0, 0, 0, 0, 0 }, bufferBytes);

        byte[] colorBytes = ReadRows(device, colorReadback, colorFootprint, 2);
        for (int offset = 0; offset < colorBytes.Length; offset += 4)
            Assert.Equal(new byte[] { 64, 128, 255, 255 }, colorBytes.AsSpan(offset, 4).ToArray());

        byte[] depthBytes = ReadRows(device, depthReadback, depthFootprint, 2);
        int depthStride = checked((int)depthFootprint.RowSizeInBytes / 2);
        for (int row = 0; row < 2; row++)
        for (int column = 0; column < 2; column++)
        {
            int offset = row * checked((int)depthFootprint.RowSizeInBytes) + column * depthStride;
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(depthBytes.AsSpan(offset, sizeof(uint))) & 0xFFFFFFu;
            Assert.InRange(raw, 0x7FFFFFu, 0x800000u);
        }

        byte[] stencilBytes = ReadRows(device, stencilReadback, stencilFootprint, 2);
        int stencilStride = checked((int)stencilFootprint.RowSizeInBytes / 2);
        for (int row = 0; row < 2; row++)
        for (int column = 0; column < 2; column++)
        {
            ReadOnlySpan<byte> texel = stencilBytes.AsSpan(
                row * checked((int)stencilFootprint.RowSizeInBytes) + column * stencilStride,
                stencilStride);
            Assert.Contains((byte)0x7B, texel.ToArray());
        }

        device.DestroyBuffer(stencilReadback);
        device.DestroyBuffer(depthReadback);
        device.DestroyTexture(depthStencil);
        device.DestroyBuffer(colorReadback);
        device.DestroyTexture(color);
        device.DestroyBuffer(bufferReadback);
        device.DestroyBuffer(clearedBuffer);
        device.CollectGarbage();
        Assert.DoesNotContain(device.DrainDiagnostics(), static diagnostic =>
            diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
    }

    private static byte[] ReadRows(Device device, BufferHandle buffer, in TextureCopyFootprint footprint, int rows)
    {
        int rowSize = checked((int)footprint.RowSizeInBytes);
        byte[] result = new byte[checked(rowSize * rows)];
        for (int row = 0; row < rows; row++)
            device.ReadBuffer(buffer, footprint.Layout.Offset + (ulong)row * footprint.Layout.BytesPerRow,
                result.AsSpan(row * rowSize, rowSize));
        return result;
    }
}
