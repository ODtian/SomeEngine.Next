using System.Numerics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class AttachmentCommandReuseTests
{
    [Fact]
    public void ClosedColorAndDepthClearsRepeatAfterTheirSubmissionCompletes()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Texture color = backend.CreateTexture(device, new(TextureDimension.Texture2D,
            1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
        using Texture depth = backend.CreateTexture(device, new(TextureDimension.Texture2D,
            1, 1, 1, 1, 1, 1, Format.D32Float, TextureUsages.DepthStencilAttachment | TextureUsages.CopySource));
        using Buffer readback = backend.CreateBuffer(device, new(768, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext clears = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        using CommandContext copies = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        TextureSubresourceRange colorRange = new(0, 1, 0, 1, TextureAspects.Color);
        TextureSubresourceRange depthRange = new(0, 1, 0, 1, TextureAspects.Depth);
        backend.Begin(copies);
        backend.Barrier(copies, new TextureBarrier(color, colorRange, PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
        backend.Barrier(copies, new TextureBarrier(depth, depthRange, PipelineSync.None, PipelineSync.DepthStencil,
            ResourceAccess.NoAccess, ResourceAccess.DepthStencilWrite, TextureLayout.Undefined, TextureLayout.DepthStencilWrite));
        Submit(copies);

        backend.Begin(clears);
        backend.ClearTexture(clears, color, colorRange, new(1, 0, 0, 1));
        backend.ClearDepthStencil(clears, depth, depthRange, .75f);
        Submit(clears);
        Assert.True(backend.TryRepeatCommands(clears, out RecordedCommands repeated));
        using (repeated)
        {
            QueueCompletion completion = backend.Submit(queue, new([], [], [repeated], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            backend.CollectCompleted(device);
        }

        backend.Begin(copies);
        backend.Barrier(copies, new TextureBarrier(color, colorRange, PipelineSync.RenderTarget, PipelineSync.Copy,
            ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.Barrier(copies, new TextureBarrier(depth, depthRange, PipelineSync.DepthStencil, PipelineSync.Copy,
            ResourceAccess.DepthStencilWrite, ResourceAccess.CopySource, TextureLayout.DepthStencilWrite, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(copies, new(readback, 0, 256, 1, color, 0, 0, TextureAspects.Color, 0, 0, 0, 1, 1, 1));
        backend.CopyTextureToBuffer(copies, new(readback, 512, 256, 1, depth, 0, 0, TextureAspects.Depth, 0, 0, 0, 1, 1, 1));
        Submit(copies);
        using MappedBuffer pixels = backend.Map(readback, MapType.Read, BufferRange.Whole);
        pixels.Invalidate(pixels.Range);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.Bytes[..4].ToArray());
        Assert.Equal(.75f, BitConverter.ToSingle(pixels.Bytes[512..516]));

        void Submit(CommandContext context)
        {
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            backend.CollectCompleted(device);
        }
    }

    [Fact]
    public void NewColorClearRecordingsObserveTheSelectedArrayLayer()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Texture texture = backend.CreateTexture(device, new(TextureDimension.Texture2D,
            1, 1, 1, 1, 2, 1, Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
        using Buffer readback = backend.CreateBuffer(device, new(768, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext clears = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        using CommandContext copies = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        TextureSubresourceRange whole = new(0, 1, 0, 2, TextureAspects.Color);
        backend.Begin(copies);
        backend.Barrier(copies, new TextureBarrier(texture, whole, PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
        backend.ClearTexture(copies, texture, whole, new(0, 0, 1, 1));
        Submit(copies);

        for (uint layer = 0; layer < 2; layer++)
        {
            backend.Begin(clears);
            backend.ClearTexture(clears, texture, new(0, 1, layer, 1, TextureAspects.Color), new(1, 0, 0, 1));
            Submit(clears);
            backend.Begin(copies);
            backend.Barrier(copies, new TextureBarrier(texture, whole, PipelineSync.RenderTarget, PipelineSync.Copy,
                ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
            for (uint copiedLayer = 0; copiedLayer < 2; copiedLayer++)
                backend.CopyTextureToBuffer(copies, new(readback, copiedLayer * 512ul, 256, 1,
                    texture, 0, copiedLayer, TextureAspects.Color, 0, 0, 0, 1, 1, 1));
            backend.Barrier(copies, new TextureBarrier(texture, whole, PipelineSync.Copy, PipelineSync.RenderTarget,
                ResourceAccess.CopySource, ResourceAccess.RenderTarget, TextureLayout.CopySource, TextureLayout.RenderTarget));
            Submit(copies);
            using MappedBuffer pixels = backend.Map(readback, MapType.Read, BufferRange.Whole);
            pixels.Invalidate(pixels.Range);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.Bytes[..4].ToArray());
            Assert.Equal(layer == 0 ? new byte[] { 0, 0, 255, 255 } : new byte[] { 255, 0, 0, 255 },
                pixels.Bytes[512..516].ToArray());
        }

        void Submit(CommandContext context)
        {
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            backend.CollectCompleted(device);
        }
    }
}
