namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

public sealed class VulkanSparseCoordinateTests(ITestOutputHelper output)
{
    [Fact]
    public void Sparse_texture_box_mappings_preserve_nonzero_x()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(
            default, [new DeviceQueueDesc(QueueType.Graphics)],
            optionalFeatures: DeviceFeatures.SparseResources));
        RequireSparseTextureSupport(backend, device);
        SparseTileShape shape = QueryTileShape(backend, device);
        RequireDimensions(device, checked(3 * shape.Width), shape.Height, 1);
        TextureDesc description = new(
            TextureDimension.Texture2D, checked(3 * shape.Width), shape.Height, 1,
            1, 1, 1, Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopySource | TextureUsages.CopyDestination);
        using Texture source = backend.CreateReservedTexture(device, description);
        using Texture destination = backend.CreateReservedTexture(device, description);
        SparseResourceInfo sourceInfo = backend.GetSparseResourceInfo(source);
        SparseResourceInfo info = backend.GetSparseResourceInfo(destination);
        output.WriteLine($"Box geometry: source={sourceInfo}; destination={info}; size={description.Width}x{description.Height}");
        if (sourceInfo.TileShape != shape || info.TileShape != shape ||
            sourceInfo.PackedMips.StandardMipLevelCount < 1 ||
            info.PackedMips.StandardMipLevelCount < 1)
            throw SkipException.ForSkip("This adapter does not provide the required standard-mip 3 by 1 tile grid.");

        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        SparseTileCoordinate origin = new(0, 0, 0, 0);
        SparseTileCoordinate destinationStart = new(1, 0, 0, 0);
        SparseTileRegion region = new(destinationStart, 2, 1, 1, 2, Boxed: true);

        // Copy only an unmapped state. No texels from the fresh source are read.
        output.WriteLine("Copy unmapped box: source=(0,0,0,0), destination=(1,0,0,0), count=2.");
        QueueCompletion copied = backend.CopySparseMappings(
            queue, [new SparseMappingCopyDesc(destination, destinationStart, source, origin, region)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(copied, TimeSpan.FromSeconds(10)));

        using Heap heap = backend.CreateHeap(device, new HeapDesc(
            checked(info.Alignment * 2), info.Alignment, MemoryType.DeviceLocal, HeapFlags.Textures));
        QueueCompletion mapped = backend.UpdateSparseMappings(
            queue, [new SparseMappingDesc(destination, region, SparseMappingType.Mapped, heap, 0)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(mapped, TimeSpan.FromSeconds(10)));

        uint width = checked(2 * shape.Width);
        byte[] expected = CreateTexels(width, shape.Height, 0x31);
        byte[] actual = UploadAndReadTile(
            backend, device, queue, destination, 0, 0,
            shape.Width, 0, width, shape.Height, TextureLayout.Undefined, expected);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
        output.WriteLine($"Compared {actual.Length} initialized bytes from two boxed tiles.");
    }

    [Fact]
    public void Sparse_texture_linear_mappings_preserve_mip_layer_and_row_wrap()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(
            default, [new DeviceQueueDesc(QueueType.Graphics)],
            optionalFeatures: DeviceFeatures.SparseResources));
        RequireSparseTextureSupport(backend, device);
        SparseTileShape shape = QueryTileShape(backend, device);
        RequireDimensions(device, checked(4 * shape.Width), checked(4 * shape.Height), 3);
        TextureDesc description = new(
            TextureDimension.Texture2D, checked(4 * shape.Width), checked(4 * shape.Height), 1,
            2, 3, 1, Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopySource | TextureUsages.CopyDestination);
        using Texture source = backend.CreateReservedTexture(device, description);
        using Texture destination = backend.CreateReservedTexture(device, description);
        SparseResourceInfo sourceInfo = backend.GetSparseResourceInfo(source);
        SparseResourceInfo info = backend.GetSparseResourceInfo(destination);
        output.WriteLine($"Linear geometry: source={sourceInfo}; destination={info}; size={description.Width}x{description.Height}; mips=2; layers=3");
        if (sourceInfo.TileShape != shape || info.TileShape != shape ||
            sourceInfo.PackedMips.StandardMipLevelCount < 2 ||
            info.PackedMips.StandardMipLevelCount < 2)
            throw SkipException.ForSkip("This adapter does not provide two standard mips with the required 2 by 2 target tile grid.");

        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        SparseTileCoordinate origin = new(0, 0, 0, 0);
        SparseTileCoordinate destinationStart = new(1, 0, 0, 5);
        SparseTileRegion region = new(destinationStart, 0, 0, 0, 2, Boxed: false);

        // Mip 1, layer 2 has subresource 5. The range crosses into the next tile row.
        output.WriteLine("Copy unmapped linear range: source=(0,0,0,0), destination=(1,0,0,5), count=2.");
        QueueCompletion copied = backend.CopySparseMappings(
            queue, [new SparseMappingCopyDesc(destination, destinationStart, source, origin, region)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(copied, TimeSpan.FromSeconds(10)));

        using Heap heap = backend.CreateHeap(device, new HeapDesc(
            checked(info.Alignment * 2), info.Alignment, MemoryType.DeviceLocal, HeapFlags.Textures));
        QueueCompletion mapped = backend.UpdateSparseMappings(
            queue, [new SparseMappingDesc(destination, region, SparseMappingType.Mapped, heap, 0)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(mapped, TimeSpan.FromSeconds(10)));

        byte[] firstExpected = CreateTexels(shape.Width, shape.Height, 0x53);
        byte[] firstActual = UploadAndReadTile(
            backend, device, queue, destination, 1, 2,
            shape.Width, 0, shape.Width, shape.Height, TextureLayout.Undefined, firstExpected);
        Assert.NotEmpty(firstExpected);
        Assert.Equal(firstExpected, firstActual);

        byte[] secondExpected = CreateTexels(shape.Width, shape.Height, 0xA7);
        byte[] secondActual = UploadAndReadTile(
            backend, device, queue, destination, 1, 2,
            0, shape.Height, shape.Width, shape.Height, TextureLayout.CopySource, secondExpected);
        Assert.NotEmpty(secondExpected);
        Assert.Equal(secondExpected, secondActual);
        output.WriteLine($"Compared {firstActual.Length + secondActual.Length} initialized bytes across a tile row at mip 1, layer 2.");
    }

    private void RequireSparseTextureSupport(IGraphicsBackend backend, Device device)
    {
        bool sparse = backend.TryGetCapability(device, out SparseResources? capability);
        FormatSupport format = device.Capabilities.GetFormatSupport(Format.R8G8B8A8UNorm);
        output.WriteLine($"Adapter={device.Adapter.Name}; SparseResources={sparse}; RGBA8={format}; native validation=false; debug messages=false.");
        if (!sparse || capability is null || !capability.Texture2DSupported)
            throw SkipException.ForSkip("The adapter does not expose the sparse texture capability.");
        const FormatFeatures required = FormatFeatures.SparseTexture2D | FormatFeatures.ShaderSample;
        if ((format.Features & required) != required || !format.SupportsSparseSampleCount(1))
            throw SkipException.ForSkip("The adapter does not expose single-sample sampled sparse RGBA8 2D textures.");
    }

    private SparseTileShape QueryTileShape(IGraphicsBackend backend, Device device)
    {
        uint side = Math.Min(1024u, device.Capabilities.Limits.MaximumTextureDimension2D);
        Assert.True(side > 0);
        using Texture probe = backend.CreateReservedTexture(device, new TextureDesc(
            TextureDimension.Texture2D, side, side, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopySource | TextureUsages.CopyDestination));
        SparseResourceInfo info = backend.GetSparseResourceInfo(probe);
        output.WriteLine($"Probe={info}");
        Assert.True(info.Alignment > 0);
        Assert.True(info.TileShape.Width > 0);
        Assert.True(info.TileShape.Height > 0);
        if (info.TileShape.Depth != 1)
            throw SkipException.ForSkip("The adapter's 2D sparse tile depth is not one.");
        return info.TileShape;
    }

    private static void RequireDimensions(Device device, uint width, uint height, uint layers)
    {
        DeviceLimits limits = device.Capabilities.Limits;
        if (width > limits.MaximumTextureDimension2D ||
            height > limits.MaximumTextureDimension2D ||
            layers > limits.MaximumTextureArrayLayers)
            throw SkipException.ForSkip("The required sparse tile grid exceeds this adapter's texture limits.");
    }

    private static byte[] CreateTexels(uint width, uint height, byte tag)
    {
        byte[] bytes = new byte[checked((int)(width * height * 4))];
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                int offset = checked((int)((y * width + x) * 4));
                bytes[offset] = unchecked((byte)((x * 17) ^ (y * 29) ^ (x >> 3) ^ tag));
                bytes[offset + 1] = unchecked((byte)((x * 7) + (y * 11) + tag));
                bytes[offset + 2] = unchecked((byte)((x >> 1) ^ (y * 3) ^ (tag * 5)));
                bytes[offset + 3] = 0xFF;
            }
        }
        return bytes;
    }

    private static byte[] UploadAndReadTile(
        IGraphicsBackend backend, Device device, Queue queue, Texture texture,
        uint mip, uint layer, uint x, uint y, uint width, uint height,
        TextureLayout layoutBefore, byte[] expected)
    {
        using Buffer upload = backend.CreateBuffer(
            device, new BufferDesc((ulong)expected.Length, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device, new BufferDesc((ulong)expected.Length, BufferUsages.CopyDestination), MemoryType.Readback);
        using (MappedBuffer mapped = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            expected.AsSpan().CopyTo(mapped.Bytes);
            mapped.Flush(mapped.Range);
        }
        TextureSubresourceRange range = new(mip, 1, layer, 1, TextureAspects.Color);
        using CommandContext context = backend.CreateCommandContext(
            device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            texture, range, PipelineSync.Copy, PipelineSync.Copy,
            layoutBefore == TextureLayout.Undefined ? ResourceAccess.NoAccess : ResourceAccess.CopySource,
            ResourceAccess.CopyDestination, layoutBefore, TextureLayout.CopyDestination));
        backend.CopyBufferToTexture(context, new BufferTextureCopy(
            upload, 0, 0, 0, texture, mip, layer, TextureAspects.Color,
            x, y, 0, width, height, 1));
        backend.Barrier(context, new TextureBarrier(
            texture, range, PipelineSync.Copy, PipelineSync.Copy,
            ResourceAccess.CopyDestination, ResourceAccess.CopySource,
            TextureLayout.CopyDestination, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(
            readback, 0, 0, 0, texture, mip, layer, TextureAspects.Color,
            x, y, 0, width, height, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        return result.Bytes.ToArray();
    }
}
