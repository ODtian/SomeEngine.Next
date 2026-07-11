using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class TextureResolveTests
{
    [Fact]
    public void Average_color_resolve_writes_a_single_sample_texture()
    {
        using Device device = new();
        const int width = 3;
        const int height = 2;
        const float expected = 0.375f;
        TextureDesc sourceDesc = new(
            width,
            height,
            Format.R32Float,
            TextureUsage.ColorAttachment | TextureUsage.CopySource,
            SampleCount: 4,
            Name: "resolve-source");
        TextureDesc destinationDesc = new(
            width,
            height,
            Format.R32Float,
            TextureUsage.CopyDestination | TextureUsage.CopySource,
            Name: "resolve-destination");
        TextureCopyRegion copyRegion = new(0, 0, TextureAspect.Color, width, height);
        TextureCopyFootprint footprint = device.GetTextureCopyFootprint(destinationDesc, copyRegion);
        TextureHandle source = device.CreateTexture(sourceDesc);
        TextureHandle destination = device.CreateTexture(destinationDesc);
        TextureViewHandle sourceView = device.CreateTextureView(new TextureViewDesc(
            source,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.ColorAttachment,
            Dimension: TextureViewDimension.Texture2DMS));
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopyDestination),
            MemoryType.Readback);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics))
        {
            commands.Barriers([
                ResourceBarrier.Transition(source.Resource, ResourceState.Common, ResourceState.RenderTarget),
            ]);
            commands.BeginRendering(new RenderingInfo(
                new ColorAttachment[]
                {
                    new(sourceView, LoadAction.Clear, StoreAction.Store, new Vector4(expected)),
                },
                null,
                width,
                height));
            commands.EndRendering();
            commands.Barriers([
                ResourceBarrier.Transition(source.Resource, ResourceState.RenderTarget, ResourceState.ResolveSource),
                ResourceBarrier.Transition(destination.Resource, ResourceState.Common, ResourceState.ResolveDestination),
            ]);
            commands.ResolveTexture(new TextureResolveRegion(source, destination));
            commands.Barriers([
                ResourceBarrier.Transition(destination.Resource, ResourceState.ResolveDestination, ResourceState.CopySource),
            ]);
            commands.CopyTextureToBuffer(new TextureBufferCopy(
                destination,
                copyRegion,
                readback,
                footprint.Layout));
            GpuCompletion completion = device.Submit(QueueType.Graphics, [commands.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(1)));
        }

        byte[] actual = new byte[checked(width * height * sizeof(float))];
        device.ReadBuffer(readback, 0, actual);
        for (int pixel = 0; pixel < width * height; pixel++)
            Assert.Equal(expected, BitConverter.ToSingle(actual, pixel * sizeof(float)));

        device.DestroyTextureView(sourceView);
        device.DestroyTexture(destination);
        device.DestroyTexture(source);
        device.DestroyBuffer(readback);
    }

    [Fact]
    public void Linear_buffer_copies_reject_multisampled_textures_in_both_directions()
    {
        using Device device = new();
        TextureDesc desc = new(
            2,
            2,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopySource | TextureUsage.CopyDestination,
            SampleCount: 4);
        TextureCopyRegion region = new(0, 0, TextureAspect.Color, 2, 2);
        TextureHandle texture = device.CreateTexture(desc);
        BufferHandle upload = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopyDestination),
            MemoryType.Readback);

        Assert.Throws<NotSupportedException>(() => device.GetTextureCopyFootprint(desc, region));
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics))
        {
            Assert.Throws<NotSupportedException>(() => commands.CopyBufferToTexture(new BufferTextureCopy(
                upload,
                new TextureBufferLayout(0, 8, 2),
                texture,
                region)));
            Assert.Throws<NotSupportedException>(() => commands.CopyTextureToBuffer(new TextureBufferCopy(
                texture,
                region,
                readback,
                new TextureBufferLayout(0, 8, 2))));
        }

        device.DestroyBuffer(readback);
        device.DestroyBuffer(upload);
        device.DestroyTexture(texture);
    }

    [Fact]
    public void Portable_resolve_rejects_non_average_and_non_multisampled_sources()
    {
        using Device device = new();
        TextureDesc singleSampleDesc = new(
            2,
            2,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopySource | TextureUsage.CopyDestination);
        TextureDesc multisampleDesc = singleSampleDesc with { SampleCount = 4 };
        TextureHandle singleSource = device.CreateTexture(singleSampleDesc);
        TextureHandle multisampleSource = device.CreateTexture(multisampleDesc);
        TextureHandle destination = device.CreateTexture(singleSampleDesc);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics))
        {
            Assert.Throws<ArgumentException>(() =>
                commands.ResolveTexture(new TextureResolveRegion(singleSource, destination)));
            Assert.Throws<NotSupportedException>(() => commands.ResolveTexture(new TextureResolveRegion(
                multisampleSource,
                destination,
                Mode: ResolveMode.SampleZero)));
        }

        device.DestroyTexture(destination);
        device.DestroyTexture(multisampleSource);
        device.DestroyTexture(singleSource);
    }
}
