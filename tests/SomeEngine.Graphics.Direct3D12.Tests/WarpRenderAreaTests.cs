using System.Numerics;
using Xunit;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpRenderAreaTests
{
    [Fact]
    public void ClearingAnOffsetRectanglePreservesPixelsOutsideIt()
    {
        using IGraphicsBackend backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Texture texture = backend.CreateTexture(device, new TextureDesc(
            TextureDimension.Texture2D, 4, 4, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
            TextureUsages.ColorAttachment | TextureUsages.CopySource));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView view = backend.CreateColorAttachmentView(device,
            new ColorAttachmentViewDesc(texture, range, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using Buffer result = backend.CreateBuffer(device, new BufferDesc(1024, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
        backend.BeginRendering(context, new RenderingDesc(
            [new ColorAttachmentDesc(view, LoadType.Clear, StoreType.Store, new Vector4(0, 0, 1, 1))], null, 4, 4));
        backend.EndRendering(context);
        backend.BeginRendering(context, new RenderingDesc(
            [new ColorAttachmentDesc(view, LoadType.Clear, StoreType.Store, new Vector4(1, 0, 0, 1))], null, 2, 2, x: 1, y: 1));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.RenderTarget, PipelineSync.Copy,
            ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(result, 0, 256, 4, texture, 0, 0,
            TextureAspects.Color, 0, 0, 0, 4, 4, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapping = backend.Map(result, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            bool inside = x is 1 or 2 && y is 1 or 2;
            ReadOnlySpan<byte> pixel = mapping.Bytes.Slice(y * 256 + x * 4, 4);
            Assert.Equal(inside ? (byte)255 : (byte)0, pixel[0]);
            Assert.Equal(0, pixel[1]);
            Assert.Equal(inside ? (byte)0 : (byte)255, pixel[2]);
            Assert.Equal(255, pixel[3]);
        }
    }
}
