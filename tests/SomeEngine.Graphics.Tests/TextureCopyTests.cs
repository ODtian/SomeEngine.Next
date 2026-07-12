using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class TextureCopyTests
{
    [Fact]
    public void Null_copies_full_and_partial_texture_subresources_without_overlap()
    {
        using Device device = new();
        TextureDesc desc = new(4, 4, Format.R8G8B8A8UNorm,
            TextureUsage.CopySource | TextureUsage.CopyDestination);
        TextureHandle source = device.CreateTexture(desc);
        TextureHandle destination = device.CreateTexture(desc);
        TextureCopyRegion whole = new(0, 0, TextureAspect.Color, 4, 4);
        TextureCopyRegion sourcePatch = new(0, 0, TextureAspect.Color, 1, 1, 0, 2, 2, 1);
        TextureCopyRegion destinationPatch = new(0, 0, TextureAspect.Color, 2, 2);
        TextureCopyFootprint footprint = device.GetTextureCopyFootprint(desc, whole);
        BufferHandle upload = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopyDestination), MemoryType.Readback);
        byte[] expectedSource = Enumerable.Range(0, 64).Select(static value => (byte)(value + 1)).ToArray();
        device.WriteBuffer(upload, 0, expectedSource);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.Barriers([
                ResourceBarrier.Transition(source.Resource, ResourceState.Common, ResourceState.CopyDestination),
                ResourceBarrier.Transition(destination.Resource, ResourceState.Common, ResourceState.CopyDestination),
            ]);
            commands.CopyBufferToTexture(new BufferTextureCopy(upload, footprint.Layout, source, whole));
            commands.Barriers([
                ResourceBarrier.Transition(source.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
            ]);
            commands.CopyTexture(new TextureToTextureCopy(source, sourcePatch, destination, destinationPatch));
            commands.Barriers([
                ResourceBarrier.Transition(destination.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
            ]);
            commands.CopyTextureToBuffer(new TextureBufferCopy(destination, whole, readback, footprint.Layout));
            GpuCompletion completion = device.Submit(QueueType.Copy, [commands.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.Zero));
        }

        byte[] actual = new byte[64];
        device.ReadBuffer(readback, 0, actual);
        byte[] expected = new byte[64];
        for (int row = 0; row < 2; row++)
            expectedSource.AsSpan(((1 + row) * 4 + 1) * 4, 8).CopyTo(expected.AsSpan(row * 16, 8));
        Assert.Equal(expected, actual);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            Assert.Throws<InvalidOperationException>(() => commands.CopyTexture(new TextureToTextureCopy(
                source,
                new TextureCopyRegion(0, 0, TextureAspect.Color, 0, 0, 0, 3, 3, 1),
                source,
                new TextureCopyRegion(0, 0, TextureAspect.Color, 1, 1, 0, 3, 3, 1))));
        }

        device.DestroyBuffer(readback);
        device.DestroyBuffer(upload);
        device.DestroyTexture(destination);
        device.DestroyTexture(source);
    }
}
