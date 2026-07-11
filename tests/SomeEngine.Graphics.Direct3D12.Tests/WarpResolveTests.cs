using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpResolveTests
{
    [Fact]
    public void Average_color_resolve_round_trips_on_warp_without_debug_errors()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        const int width = 4;
        const int height = 3;
        Vector4 clear = new(0.25f, 0.5f, 0.75f, 1f);
        TextureDesc sourceDesc = new(
            width,
            height,
            Format.R8G8B8A8UNorm,
            TextureUsage.ColorAttachment | TextureUsage.CopySource,
            SampleCount: 4,
            Name: "warp-resolve-source");
        TextureDesc destinationDesc = new(
            width,
            height,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopyDestination | TextureUsage.CopySource,
            Name: "warp-resolve-destination");
        TextureCopyRegion copyRegion = new(0, 0, TextureAspect.Color, width, height);
        TextureCopyFootprint footprint = device.GetTextureCopyFootprint(destinationDesc, copyRegion, 19);
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

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics, "warp-resolve"))
        {
            commands.Barriers([
                ResourceBarrier.Transition(source.Resource, ResourceState.Common, ResourceState.RenderTarget),
            ]);
            commands.BeginRendering(new RenderingInfo(
                new ColorAttachment[]
                {
                    new(sourceView, LoadAction.Clear, StoreAction.Store, clear),
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
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));
        }

        byte[] storage = new byte[checked((int)footprint.RequiredBufferSize)];
        device.ReadBuffer(readback, 0, storage);
        byte[] expected = [64, 128, 191, 255];
        for (int row = 0; row < height; row++)
        for (int column = 0; column < width; column++)
        {
            int offset = checked(
                (int)footprint.Layout.Offset +
                row * (int)footprint.Layout.BytesPerRow +
                column * 4);
            for (int component = 0; component < 4; component++)
                Assert.InRange(storage[offset + component], expected[component] - 1, expected[component] + 1);
        }
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);

        device.DestroyTextureView(sourceView);
        device.DestroyTexture(destination);
        device.DestroyTexture(source);
        device.DestroyBuffer(readback);
        device.CollectGarbage();
    }

    [Fact]
    public void Multisampled_texture_still_cannot_use_a_linear_buffer_copy_on_warp()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options { UseWarpAdapter = true });
        TextureDesc desc = new(
            2,
            2,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopySource | TextureUsage.CopyDestination | TextureUsage.ColorAttachment,
            SampleCount: 4);
        TextureCopyRegion region = new(0, 0, TextureAspect.Color, 2, 2);
        TextureHandle texture = device.CreateTexture(desc);
        BufferHandle upload = device.CreateBuffer(
            new BufferDesc(512, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(512, BufferUsage.CopyDestination),
            MemoryType.Readback);

        Assert.Throws<NotSupportedException>(() => device.GetTextureCopyFootprint(desc, region));
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics))
        {
            Assert.Throws<NotSupportedException>(() => commands.CopyBufferToTexture(new BufferTextureCopy(
                upload,
                new TextureBufferLayout(0, 256, 2),
                texture,
                region)));
            Assert.Throws<NotSupportedException>(() => commands.CopyTextureToBuffer(new TextureBufferCopy(
                texture,
                region,
                readback,
                new TextureBufferLayout(0, 256, 2))));
        }

        device.DestroyBuffer(readback);
        device.DestroyBuffer(upload);
        device.DestroyTexture(texture);
    }
}
