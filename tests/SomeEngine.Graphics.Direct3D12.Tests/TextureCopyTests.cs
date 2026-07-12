using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class TextureCopyTests
{
    [Fact]
    public void Warp_copies_full_and_partial_texture_subresources_with_real_readback()
    {
        Assert.True(OperatingSystem.IsWindows());
        using Device device = CreateDevice();
        TextureDesc desc = new(4, 4, Format.R8G8B8A8UNorm,
            TextureUsage.CopySource | TextureUsage.CopyDestination);
        TextureHandle baseSource = device.CreateTexture(desc);
        TextureHandle patchSource = device.CreateTexture(desc);
        TextureHandle destination = device.CreateTexture(desc);
        TextureCopyRegion whole = new(0, 0, TextureAspect.Color, 4, 4);
        TextureCopyRegion sourcePatch = new(0, 0, TextureAspect.Color, 1, 1, 0, 2, 2, 1);
        TextureCopyRegion destinationPatch = new(0, 0, TextureAspect.Color, 2, 2);
        TextureCopyFootprint footprint = device.GetTextureCopyFootprint(desc, whole);
        BufferHandle baseUpload = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle patchUpload = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopyDestination), MemoryType.Readback);
        byte[] baseBytes = Pattern(4, 4, 7);
        byte[] patchBytes = Pattern(4, 4, 151);
        WriteRows(device, baseUpload, footprint, baseBytes, 16, 4);
        WriteRows(device, patchUpload, footprint, patchBytes, 16, 4);

        GpuCompletion completion;
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy, "texture-copy"))
        {
            commands.Barriers([
                ResourceBarrier.Transition(baseSource.Resource, ResourceState.Common, ResourceState.CopyDestination),
                ResourceBarrier.Transition(patchSource.Resource, ResourceState.Common, ResourceState.CopyDestination),
                ResourceBarrier.Transition(destination.Resource, ResourceState.Common, ResourceState.CopyDestination),
            ]);
            commands.CopyBufferToTexture(new BufferTextureCopy(baseUpload, footprint.Layout, baseSource, whole));
            commands.CopyBufferToTexture(new BufferTextureCopy(patchUpload, footprint.Layout, patchSource, whole));
            commands.Barriers([
                ResourceBarrier.Transition(baseSource.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
                ResourceBarrier.Transition(patchSource.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
            ]);
            commands.CopyTexture(new TextureToTextureCopy(baseSource, whole, destination, whole));
            commands.CopyTexture(new TextureToTextureCopy(patchSource, sourcePatch, destination, destinationPatch));
            commands.Barriers([
                ResourceBarrier.Transition(destination.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
            ]);
            commands.CopyTextureToBuffer(new TextureBufferCopy(destination, whole, readback, footprint.Layout));
            completion = device.Submit(QueueType.Copy, [commands.Finish()]);
        }
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        byte[] actual = ReadRows(device, readback, footprint, 16, 4);
        byte[] expected = baseBytes.ToArray();
        for (int row = 0; row < 2; row++)
            patchBytes.AsSpan(((1 + row) * 4 + 1) * 4, 8).CopyTo(expected.AsSpan(row * 16, 8));
        Assert.Equal(expected, actual);

        using (ICommandContext overlap = device.AcquireCommandContext(QueueType.Copy))
        {
            Assert.Throws<ArgumentException>(() => overlap.CopyTexture(new TextureToTextureCopy(
                baseSource,
                new TextureCopyRegion(0, 0, TextureAspect.Color, 0, 0, 0, 3, 3, 1),
                baseSource,
                new TextureCopyRegion(0, 0, TextureAspect.Color, 1, 1, 0, 3, 3, 1))));
        }

        device.DestroyBuffer(readback);
        device.DestroyBuffer(patchUpload);
        device.DestroyBuffer(baseUpload);
        device.DestroyTexture(destination);
        device.DestroyTexture(patchSource);
        device.DestroyTexture(baseSource);
        device.CollectGarbage();
        AssertNoErrors(device);
    }

    private static Device CreateDevice() => new(new Options
    {
        UseWarpAdapter = true,
        EnableDebugLayer = true,
        EnableGpuValidation = false,
    });

    private static byte[] Pattern(int width, int height, byte seed)
    {
        byte[] bytes = new byte[width * height * 4];
        for (int index = 0; index < bytes.Length; index++) bytes[index] = unchecked((byte)(seed + index * 13));
        return bytes;
    }

    private static void WriteRows(Device device, BufferHandle buffer, in TextureCopyFootprint footprint, byte[] bytes, int rowBytes, int rows)
    {
        for (int row = 0; row < rows; row++)
            device.WriteBuffer(buffer, footprint.Layout.Offset + (ulong)row * footprint.Layout.BytesPerRow,
                bytes.AsSpan(row * rowBytes, rowBytes));
    }

    private static byte[] ReadRows(Device device, BufferHandle buffer, in TextureCopyFootprint footprint, int rowBytes, int rows)
    {
        byte[] bytes = new byte[rowBytes * rows];
        for (int row = 0; row < rows; row++)
            device.ReadBuffer(buffer, footprint.Layout.Offset + (ulong)row * footprint.Layout.BytesPerRow,
                bytes.AsSpan(row * rowBytes, rowBytes));
        return bytes;
    }

    private static void AssertNoErrors(Device device) => Assert.DoesNotContain(
        device.DrainDiagnostics(),
        static diagnostic => diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
}
