using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpCopyTests
{
    [Fact]
    public void Texture_copy_round_trip_respects_backend_footprints_and_partial_regions()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        TextureDesc textureDesc = new(
            8,
            6,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopySource | TextureUsage.CopyDestination,
            Name: "portable-copy-texture");
        TextureCopyRegion whole = new(0, 0, TextureAspect.Color, 8, 6);
        TextureCopyRegion patch = new(0, 0, TextureAspect.Color, 2, 1, 0, 3, 2, 1);
        TextureCopyFootprint wholeUpload = device.GetTextureCopyFootprint(textureDesc, whole, requestedBufferOffset: 37);
        TextureCopyFootprint patchUpload = device.GetTextureCopyFootprint(
            textureDesc,
            patch,
            checked(wholeUpload.RequiredBufferSize + 19));
        TextureCopyFootprint wholeReadback = device.GetTextureCopyFootprint(textureDesc, whole, requestedBufferOffset: 83);
        TextureCopyFootprint patchReadback = device.GetTextureCopyFootprint(
            textureDesc,
            patch,
            checked(wholeReadback.RequiredBufferSize + 23));

        Assert.Equal(0ul, wholeUpload.Layout.Offset % 512);
        Assert.Equal(0u, wholeUpload.Layout.BytesPerRow % 256);
        Assert.Equal(32u, wholeUpload.RowSizeInBytes);
        Assert.True(patchUpload.Layout.Offset >= wholeUpload.RequiredBufferSize);
        Assert.Equal(12u, patchUpload.RowSizeInBytes);

        BufferHandle upload = device.CreateBuffer(
            new BufferDesc(patchUpload.RequiredBufferSize, BufferUsage.CopySource, "portable-copy-upload"),
            MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(patchReadback.RequiredBufferSize, BufferUsage.CopyDestination, "portable-copy-readback"),
            MemoryType.Readback);
        TextureHandle texture = device.CreateTexture(textureDesc);

        byte[] expected = CreateRgbaPattern(textureDesc.Width, textureDesc.Height);
        WriteRows(device, upload, wholeUpload, expected, textureDesc.Width * 4, textureDesc.Height);

        byte[] patchBytes = new byte[patch.Width * patch.Height * 4];
        for (int pixel = 0; pixel < patch.Width * patch.Height; pixel++)
        {
            int offset = pixel * 4;
            patchBytes[offset] = checked((byte)(201 + pixel));
            patchBytes[offset + 1] = checked((byte)(151 + pixel));
            patchBytes[offset + 2] = checked((byte)(101 + pixel));
            patchBytes[offset + 3] = 255;
        }
        WriteRows(device, upload, patchUpload, patchBytes, patch.Width * 4, patch.Height);
        OverwriteRgbaRegion(expected, textureDesc.Width, patch, patchBytes);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy, "portable-texture-copy"))
        {
            commands.Barriers([
                ResourceBarrier.Transition(texture.Resource, ResourceState.Common, ResourceState.CopyDestination),
            ]);
            commands.CopyBufferToTexture(new BufferTextureCopy(
                upload,
                wholeUpload.Layout,
                texture,
                whole));
            commands.CopyBufferToTexture(new BufferTextureCopy(
                upload,
                patchUpload.Layout,
                texture,
                patch));
            commands.Barriers([
                ResourceBarrier.Transition(texture.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
            ]);
            commands.CopyTextureToBuffer(new TextureBufferCopy(
                texture,
                whole,
                readback,
                wholeReadback.Layout));
            commands.CopyTextureToBuffer(new TextureBufferCopy(
                texture,
                patch,
                readback,
                patchReadback.Layout));
            GpuCompletion completion = device.Submit(QueueType.Copy, [commands.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));
        }

        byte[] wholeActual = ReadRows(device, readback, wholeReadback, textureDesc.Width * 4, textureDesc.Height);
        byte[] patchActual = ReadRows(device, readback, patchReadback, patch.Width * 4, patch.Height);
        Assert.Equal(expected, wholeActual);
        Assert.Equal(patchBytes, patchActual);
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);

        device.DestroyTexture(texture);
        device.DestroyBuffer(readback);
        device.DestroyBuffer(upload);
        device.CollectGarbage();
    }

    [Fact]
    public void Depth_only_dsv_clear_round_trips_through_the_depth_plane_and_pins_the_view()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        const int width = 5;
        const int height = 3;
        const float clearDepth = 0.375f;
        TextureDesc depthDesc = new(
            width,
            height,
            Format.D32Float,
            TextureUsage.DepthStencilAttachment | TextureUsage.CopySource,
            Name: "depth-only-clear");
        TextureCopyRegion depthRegion = new(0, 0, TextureAspect.Depth, width, height);
        TextureCopyFootprint readbackFootprint = device.GetTextureCopyFootprint(depthDesc, depthRegion, 41);
        TextureHandle depth = device.CreateTexture(depthDesc);
        TextureViewHandle depthView = device.CreateTextureView(new TextureViewDesc(
            depth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth),
            TextureViewUsage.DepthStencilAttachment,
            Name: "depth-only-dsv"));
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(readbackFootprint.RequiredBufferSize, BufferUsage.CopyDestination, "depth-only-readback"),
            MemoryType.Readback);

        CommandListHandle commandList;
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics, "depth-only-clear"))
        {
            commands.Barriers([
                ResourceBarrier.Transition(
                    depth.Resource,
                    ResourceState.Common,
                    ResourceState.DepthWrite,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth)),
            ]);
            commands.BeginRendering(new RenderingInfo(
                Array.Empty<ColorAttachment>(),
                new DepthStencilAttachment(
                    depthView,
                    new DepthAttachmentOperations(LoadAction.Clear, StoreAction.Store, ClearValue: clearDepth)),
                width,
                height));
            commands.EndRendering();
            commands.Barriers([
                ResourceBarrier.Transition(
                    depth.Resource,
                    ResourceState.DepthWrite,
                    ResourceState.CopySource,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth)),
            ]);
            commands.CopyTextureToBuffer(new TextureBufferCopy(
                depth,
                depthRegion,
                readback,
                readbackFootprint.Layout));
            commandList = commands.Finish();
        }

        Assert.Throws<InvalidOperationException>(() => device.DestroyTextureView(depthView));
        GpuCompletion completion = device.Submit(QueueType.Graphics, [commandList]);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        byte[] actual = ReadRows(device, readback, readbackFootprint, width * sizeof(float), height);
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            Assert.Equal(clearDepth, BitConverter.ToSingle(actual, pixel * sizeof(float)));
        }
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);

        device.DestroyTextureView(depthView);
        device.DestroyTexture(depth);
        device.DestroyBuffer(readback);
        device.CollectGarbage();
    }

    [Fact]
    public void D24_depth_only_dsv_does_not_require_an_undeclared_stencil_operation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        TextureDesc desc = new(
            4,
            4,
            Format.D24UNormS8UInt,
            TextureUsage.DepthStencilAttachment,
            Name: "d24-depth-only");
        TextureHandle texture = device.CreateTexture(desc);
        TextureViewHandle view = device.CreateTextureView(new TextureViewDesc(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth),
            TextureViewUsage.DepthStencilAttachment));

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics))
        {
            commands.Barriers([
                ResourceBarrier.Transition(
                    texture.Resource,
                    ResourceState.Common,
                    ResourceState.DepthWrite,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth)),
            ]);
            commands.BeginRendering(new RenderingInfo(
                Array.Empty<ColorAttachment>(),
                new DepthStencilAttachment(
                    view,
                    new DepthAttachmentOperations(LoadAction.Clear, StoreAction.Store, ClearValue: 0.625f)),
                4,
                4));
            commands.EndRendering();
            GpuCompletion completion = device.Submit(QueueType.Graphics, [commands.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));
        }

        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
        device.DestroyTextureView(view);
        device.DestroyTexture(texture);
        device.CollectGarbage();
    }

    [Fact]
    public void D24_depth_clear_preserves_the_independent_stencil_plane_on_warp()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        const int width = 5;
        const int height = 3;
        const float clearDepth = 0.25f;
        TextureDesc desc = new(
            width,
            height,
            Format.D24UNormS8UInt,
            TextureUsage.CopySource | TextureUsage.CopyDestination | TextureUsage.DepthStencilAttachment,
            Name: "d24-plane-round-trip");
        TextureCopyRegion depthRegion = new(0, 0, TextureAspect.Depth, width, height);
        TextureCopyRegion stencilRegion = new(0, 0, TextureAspect.Stencil, width, height);
        TextureCopyFootprint depthFootprint = device.GetTextureCopyFootprint(desc, depthRegion, 17);
        TextureCopyFootprint stencilFootprint = device.GetTextureCopyFootprint(desc, stencilRegion, 29);
        TextureHandle texture = device.CreateTexture(desc);
        TextureViewHandle view = device.CreateTextureView(new TextureViewDesc(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth | TextureAspect.Stencil),
            TextureViewUsage.DepthStencilAttachment));
        BufferHandle stencilUpload = device.CreateBuffer(
            new BufferDesc(stencilFootprint.RequiredBufferSize, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle depthReadback = device.CreateBuffer(
            new BufferDesc(depthFootprint.RequiredBufferSize, BufferUsage.CopyDestination),
            MemoryType.Readback);
        BufferHandle stencilReadback = device.CreateBuffer(
            new BufferDesc(stencilFootprint.RequiredBufferSize, BufferUsage.CopyDestination),
            MemoryType.Readback);
        byte[] expectedStencil = Enumerable.Repeat((byte)0xa7, width * height).ToArray();
        WriteRows(device, stencilUpload, stencilFootprint, expectedStencil, width, height);

        TextureSubresourceRange depthRange = new(0, 1, 0, 1, TextureAspect.Depth);
        TextureSubresourceRange stencilRange = new(0, 1, 0, 1, TextureAspect.Stencil);
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics))
        {
            commands.Barriers([
                ResourceBarrier.Transition(texture.Resource, ResourceState.Common, ResourceState.DepthWrite, depthRange),
                ResourceBarrier.Transition(texture.Resource, ResourceState.Common, ResourceState.CopyDestination, stencilRange),
            ]);
            commands.CopyBufferToTexture(new BufferTextureCopy(
                stencilUpload,
                stencilFootprint.Layout,
                texture,
                stencilRegion));
            commands.Barriers([
                ResourceBarrier.Transition(texture.Resource, ResourceState.CopyDestination, ResourceState.DepthRead, stencilRange),
            ]);
            commands.BeginRendering(new RenderingInfo(
                Array.Empty<ColorAttachment>(),
                new DepthStencilAttachment(
                    view,
                    new DepthAttachmentOperations(LoadAction.Clear, StoreAction.Store, ClearValue: clearDepth),
                    new StencilAttachmentOperations(LoadAction.Load, StoreAction.Store, ReadOnly: true)),
                width,
                height));
            commands.EndRendering();
            commands.Barriers([
                ResourceBarrier.Transition(texture.Resource, ResourceState.DepthWrite, ResourceState.CopySource, depthRange),
                ResourceBarrier.Transition(texture.Resource, ResourceState.DepthRead, ResourceState.CopySource, stencilRange),
            ]);
            commands.CopyTextureToBuffer(new TextureBufferCopy(
                texture,
                depthRegion,
                depthReadback,
                depthFootprint.Layout));
            commands.CopyTextureToBuffer(new TextureBufferCopy(
                texture,
                stencilRegion,
                stencilReadback,
                stencilFootprint.Layout));
            GpuCompletion completion = device.Submit(QueueType.Graphics, [commands.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));
        }

        byte[] actualDepth = ReadRows(device, depthReadback, depthFootprint, width * sizeof(uint), height);
        byte[] actualStencil = ReadRows(device, stencilReadback, stencilFootprint, width, height);
        Assert.Equal(expectedStencil, actualStencil);
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            uint encoded = BitConverter.ToUInt32(actualDepth, pixel * sizeof(uint)) & 0x00ff_ffffu;
            float decoded = encoded / 16_777_215f;
            Assert.InRange(decoded, clearDepth - 1e-6f, clearDepth + 1e-6f);
        }
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);

        device.DestroyTextureView(view);
        device.DestroyTexture(texture);
        device.DestroyBuffer(stencilReadback);
        device.DestroyBuffer(depthReadback);
        device.DestroyBuffer(stencilUpload);
        device.CollectGarbage();
    }

    [Fact]
    public void Handles_and_completions_are_scoped_to_their_native_device()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device first = new(new Options { UseWarpAdapter = true });
        using Device second = new(new Options { UseWarpAdapter = true });
        Assert.NotEqual(first.Domain, second.Domain);

        BufferHandle firstBuffer = first.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopySource),
            MemoryType.Upload);
        Assert.Throws<ArgumentException>(() => second.WriteBuffer(firstBuffer, 0, [1]));

        CommandListHandle foreignList;
        using (ICommandContext commands = first.AcquireCommandContext(QueueType.Copy))
        {
            foreignList = commands.Finish();
        }
        Assert.Throws<ArgumentException>(() => second.DiscardCommandList(foreignList));
        first.DiscardCommandList(foreignList);

        GpuCompletion firstCompletion;
        using (ICommandContext commands = first.AcquireCommandContext(QueueType.Copy))
        {
            firstCompletion = first.Submit(QueueType.Copy, [commands.Finish()]);
        }

        CommandListHandle secondList;
        using (ICommandContext commands = second.AcquireCommandContext(QueueType.Copy))
        {
            secondList = commands.Finish();
        }
        GpuCompletion notYetPublished = new(second.Domain, QueueType.Copy, 1);
        Assert.Throws<ArgumentException>(() =>
            second.Submit(QueueType.Copy, [secondList], [notYetPublished]));
        Assert.Throws<ArgumentException>(() =>
            second.Submit(QueueType.Copy, [secondList], [firstCompletion]));

        GpuCompletion secondCompletion = second.Submit(QueueType.Copy, [secondList]);
        GpuCompletion unpublished = new(second.Domain, QueueType.Copy, checked(secondCompletion.Value + 1));
        Assert.Throws<ArgumentException>(() => second.Wait(unpublished, TimeSpan.Zero));

        ulong completedValue = 0;
        bool waited = false;
        Exception? workerFailure = null;
        Thread worker = new(() =>
        {
            try
            {
                waited = second.Wait(secondCompletion, TimeSpan.FromSeconds(5));
                completedValue = second.GetCompletedValue(QueueType.Copy);
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
        });
        worker.Start();
        worker.Join();
        Assert.Null(workerFailure);
        Assert.True(waited);
        Assert.True(completedValue >= secondCompletion.Value);

        Assert.True(first.Wait(firstCompletion, TimeSpan.FromSeconds(5)));
        first.DestroyBuffer(firstBuffer);
    }

    [Fact]
    public void Copy_queue_round_trips_native_memory_and_reuses_retired_context()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });

        Assert.Equal(BackendKind.Direct3D12, device.Info.Backend);
        Assert.False(device.Info.HardwareAccelerated);
        Assert.True(device.Compilation.Supports(QueueType.Copy));

        BufferHandle upload = device.CreateBuffer(
            new BufferDesc(256, BufferUsage.CopySource, "copy-smoke-upload"),
            MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(256, BufferUsage.CopyDestination, "copy-smoke-readback"),
            MemoryType.Readback);

        try
        {
            RoundTrip(device, upload, readback, seed: 17);
            RoundTrip(device, upload, readback, seed: 91);

            GraphicsDiagnostic[] diagnostics = device.DrainDiagnostics();
            Assert.DoesNotContain(diagnostics, static item =>
                item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
            device.CollectGarbage();
        }
    }

    [Fact]
    public void Buffer_requirements_come_from_the_native_device()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options { UseWarpAdapter = true, EnableDebugLayer = true });
        ResourceRequirements requirements = device.GetBufferRequirements(
            new BufferDesc(257, BufferUsage.CopySource | BufferUsage.CopyDestination),
            MemoryType.DeviceLocal);

        Assert.True(requirements.Size >= 257);
        Assert.True(requirements.Alignment >= 64 * 1024);
        Assert.Equal(ResourceHeapClass.Buffer, requirements.ResourceClass);
    }

    [Fact]
    public void Placed_resource_is_released_before_its_native_heap()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
        });
        BufferDesc placedDesc = new(256, BufferUsage.CopySource);
        ResourceRequirements requirements = device.GetBufferRequirements(placedDesc, MemoryType.DeviceLocal);
        HeapHandle heap = device.CreateHeap(new HeapDesc(
            requirements.Size,
            MemoryType.DeviceLocal,
            requirements.ResourceClass));
        BufferHandle placed = device.CreatePlacedBuffer(heap, 0, placedDesc);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(256, BufferUsage.CopyDestination),
            MemoryType.Readback);

        using ICommandContext commands = device.AcquireCommandContext(QueueType.Copy);
        commands.Barriers([
            ResourceBarrier.Transition(placed.Resource, ResourceState.Common, ResourceState.CopySource),
        ]);
        commands.CopyBuffer(placed, 0, readback, 0, 256);
        GpuCompletion completion = device.Submit(QueueType.Copy, [commands.Finish()]);

        device.DestroyBuffer(placed);
        device.DestroyHeap(heap);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(5)));
        // WARP may complete before logical destruction, in which case resources retire immediately;
        // the submitted allocator is still reclaimed by this collection.
        Assert.True(device.CollectGarbage() >= 1);

        GraphicsDiagnostic[] diagnostics = device.DrainDiagnostics();
        Assert.DoesNotContain(diagnostics, static item =>
            item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
        device.DestroyBuffer(readback);
    }

    [Fact]
    public void Failed_destruction_of_an_unsubmitted_resource_preserves_its_handle()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options { UseWarpAdapter = true });
        BufferHandle source = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle destination = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopyDestination),
            MemoryType.Readback);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.CopyBuffer(source, 0, destination, 0, 16);
            Assert.Throws<InvalidOperationException>(() => device.DestroyBuffer(source));
        }

        device.WriteBuffer(source, 0, [42]);
        device.DestroyBuffer(destination);
        device.DestroyBuffer(source);
    }

    private static void RoundTrip(Device device, BufferHandle upload, BufferHandle readback, byte seed)
    {
        byte[] expected = new byte[256];
        for (int index = 0; index < expected.Length; index++)
        {
            expected[index] = unchecked((byte)(seed + index * 37));
        }

        device.WriteBuffer(upload, 0, expected);
        using ICommandContext commands = device.AcquireCommandContext(QueueType.Copy, $"copy-{seed}");
        commands.CopyBuffer(upload, 0, readback, 0, (ulong)expected.Length);
        CommandListHandle commandList = commands.Finish();
        GpuCompletion completion = device.Submit(QueueType.Copy, [commandList]);

        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(5)));
        Assert.True(device.GetCompletedValue(QueueType.Copy) >= completion.Value);
        device.CollectGarbage();

        byte[] actual = new byte[expected.Length];
        device.ReadBuffer(readback, 0, actual);
        Assert.Equal(expected, actual);
    }

    private static byte[] CreateRgbaPattern(int width, int height)
    {
        byte[] result = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                result[offset] = checked((byte)(7 + x * 11 + y * 3));
                result[offset + 1] = checked((byte)(13 + x * 5 + y * 17));
                result[offset + 2] = checked((byte)(19 + x * 7 + y * 9));
                result[offset + 3] = 255;
            }
        }
        return result;
    }

    private static void OverwriteRgbaRegion(
        byte[] destination,
        int destinationWidth,
        in TextureCopyRegion region,
        byte[] source)
    {
        int rowBytes = region.Width * 4;
        for (int row = 0; row < region.Height; row++)
        {
            source.AsSpan(row * rowBytes, rowBytes).CopyTo(
                destination.AsSpan(((region.Y + row) * destinationWidth + region.X) * 4, rowBytes));
        }
    }

    private static void WriteRows(
        Device device,
        BufferHandle buffer,
        in TextureCopyFootprint footprint,
        byte[] source,
        int rowBytes,
        int rowCount)
    {
        Assert.Equal(checked(rowBytes * rowCount), source.Length);
        for (int row = 0; row < rowCount; row++)
        {
            device.WriteBuffer(
                buffer,
                checked(footprint.Layout.Offset + (ulong)row * footprint.Layout.BytesPerRow),
                source.AsSpan(row * rowBytes, rowBytes));
        }
    }

    private static byte[] ReadRows(
        Device device,
        BufferHandle buffer,
        in TextureCopyFootprint footprint,
        int rowBytes,
        int rowCount)
    {
        byte[] result = new byte[checked(rowBytes * rowCount)];
        for (int row = 0; row < rowCount; row++)
        {
            device.ReadBuffer(
                buffer,
                checked(footprint.Layout.Offset + (ulong)row * footprint.Layout.BytesPerRow),
                result.AsSpan(row * rowBytes, rowBytes));
        }
        return result;
    }
}
