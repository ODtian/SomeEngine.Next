namespace SomeEngine.Graphics.Vulkan.Tests;

using System.Buffers.Binary;
using SomeEngine.Tests;
using Xunit;

public sealed class VulkanReadOnlyDepthTests
{
    [Fact]
    public void ReadOnlyDepthAttachmentPreservesClearedDepth()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create(
            new VulkanBackendOptions(EnableValidation: true, EnableDebugMessages: true));
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        TextureDesc textureDesc = new(
            TextureDimension.Texture2D,
            1,
            1,
            1,
            1,
            1,
            1,
            Format.D32Float,
            TextureUsages.DepthStencilAttachment | TextureUsages.CopySource);
        using Texture texture = backend.CreateTexture(device, textureDesc);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Depth);
        using DepthStencilView writable = backend.CreateDepthStencilView(
            device,
            new DepthStencilViewDesc(texture, range, Format.D32Float, TextureViewDimension.Texture2D));
        using DepthStencilView readOnly = backend.CreateDepthStencilView(
            device,
            new DepthStencilViewDesc(
                texture,
                range,
                Format.D32Float,
                TextureViewDimension.Texture2D,
                ReadOnlyDepth: true));
        uint rowPitch = Math.Max((uint)sizeof(float), device.Capabilities.Limits.TextureDataPitchAlignment);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(rowPitch, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            texture,
            range,
            PipelineSync.None,
            PipelineSync.DepthStencil,
            ResourceAccess.NoAccess,
            ResourceAccess.DepthStencilWrite,
            TextureLayout.Undefined,
            TextureLayout.DepthStencilWrite));
        DepthStencilAttachmentDesc clear = new(
            writable,
            LoadType.Clear,
            StoreType.Store,
            LoadType.Discard,
            StoreType.Discard,
            ClearDepth: .25f);
        backend.BeginRendering(context, new RenderingDesc([], clear, 1, 1));
        backend.EndRendering(context);

        backend.Barrier(context, new TextureBarrier(
            texture,
            range,
            PipelineSync.DepthStencil,
            PipelineSync.DepthStencil,
            ResourceAccess.DepthStencilWrite,
            ResourceAccess.DepthStencilRead,
            TextureLayout.DepthStencilWrite,
            TextureLayout.DepthStencilRead));
        DepthStencilAttachmentDesc preserve = new(
            readOnly,
            LoadType.Load,
            StoreType.Store,
            LoadType.Discard,
            StoreType.Discard);
        backend.BeginRendering(context, new RenderingDesc([], preserve, 1, 1));
        backend.EndRendering(context);

        backend.Barrier(context, new TextureBarrier(
            texture,
            range,
            PipelineSync.DepthStencil,
            PipelineSync.Copy,
            ResourceAccess.DepthStencilRead,
            ResourceAccess.CopySource,
            TextureLayout.DepthStencilRead,
            TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(
            readback,
            0,
            rowPitch,
            1,
            texture,
            0,
            0,
            TextureAspects.Depth,
            0,
            0,
            0,
            1,
            1,
            1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));

        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        byte[] expected = new byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(expected, .25f);
        ImageArtifacts.AssertEqual(
            new TestImage(1, 1, Format.D32Float, expected),
            new TestImage(1, 1, Format.D32Float, mapped.Bytes[..sizeof(float)].ToArray()));
    }
}
