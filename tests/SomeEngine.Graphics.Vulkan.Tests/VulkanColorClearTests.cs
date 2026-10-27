using System.Numerics;
using SomeEngine.Tests;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
public sealed class VulkanColorClearTests
{
    [Theory]
    [InlineData(Format.R32UInt, 7f, 7u)]
    [InlineData(Format.R32SInt, 7f, 7u)]
    [InlineData(Format.R32SInt, -7f, 0xfffffff9u)]
    [InlineData(Format.R32Float, .25f, 0x3e800000u)]
    public void ColorAttachmentClearUsesTheNumericTypeOfTheView(Format format, float clearValue, uint expectedTexel)
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Texture texture = backend.CreateTexture(device, new TextureDesc(
            TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, format,
            TextureUsages.ColorAttachment | TextureUsages.CopySource));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(device,
            new ColorAttachmentViewDesc(texture, range, format, TextureViewDimension.Texture2D));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(256, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.None, PipelineSync.RenderTarget, ResourceAccess.NoAccess, ResourceAccess.RenderTarget,
            TextureLayout.Undefined, TextureLayout.RenderTarget));
        ColorAttachmentDesc[] colors = [new(attachment, LoadType.Clear, StoreType.Store, new Vector4(clearValue, 0, 0, 0))];
        backend.BeginRendering(context, new RenderingDesc(colors, null, 1, 1));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.RenderTarget, PipelineSync.Copy, ResourceAccess.RenderTarget, ResourceAccess.CopySource,
            TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(readback, 0, 256, 1,
            texture, 0, 0, TextureAspects.Color, 0, 0, 0, 1, 1, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        ImageArtifacts.AssertEqual(new(1, 1, format, BitConverter.GetBytes(expectedTexel)),
            new(1, 1, format, mapped.Bytes[..4].ToArray()));
    }
}
