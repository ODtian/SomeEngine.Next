using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpExternalTests
{
    [Fact]
    public void External_handle_support_is_reported_per_object_family_and_direction()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.True(backend.TryGetCapability(device, out ExternalResources? resources));
        Assert.NotNull(resources);
        Assert.Equal(ExternalHandleTypes.OpaqueWin32, resources.BufferImportHandleTypes);
        Assert.Equal(ExternalHandleTypes.OpaqueWin32, resources.BufferExportHandleTypes);
        Assert.Equal(ExternalHandleTypes.OpaqueWin32, resources.TextureImportHandleTypes);
        Assert.Equal(ExternalHandleTypes.OpaqueWin32, resources.TextureExportHandleTypes);
        Assert.Equal(ExternalHandleTypes.OpaqueWin32, resources.HeapImportHandleTypes);
        Assert.Equal(ExternalHandleTypes.OpaqueWin32, resources.HeapExportHandleTypes);

        Assert.True(backend.TryGetCapability(device, out ExternalTimelines? timelines));
        Assert.NotNull(timelines);
        Assert.Equal(ExternalHandleTypes.OpaqueWin32, timelines.ImportHandleTypes);
        Assert.Equal(ExternalHandleTypes.OpaqueWin32, timelines.ExportHandleTypes);
    }

    [Fact]
    public void Imported_timeline_preserves_existing_value_and_orders_queue_work()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using ExternalTimeline source = backend.CreateExternalTimeline(device, initialValue: 9);
        using ExternalHandle handle = backend.ExportTimeline(
            source,
            ExternalHandleType.OpaqueWin32);
        using ExternalTimeline imported = backend.ImportTimeline(
            device,
            handle);
        handle.Dispose();
        Queue queue = backend.GetQueue(device, QueueType.Graphics);

        QueueCompletion initialWait = backend.Submit(
            queue,
            new QueueSubmitDesc(
                [],
                [new TimelinePoint(imported, 9)],
                [],
                [],
                []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(initialWait, TimeSpan.FromSeconds(10)));

        QueueCompletion signal = backend.Submit(
            queue,
            new QueueSubmitDesc(
                [],
                [],
                [],
                [],
                [new TimelineSignal(source, 10)]));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(signal, TimeSpan.FromSeconds(10)));

        QueueCompletion importedWait = backend.Submit(
            queue,
            new QueueSubmitDesc(
                [],
                [new TimelinePoint(imported, 10)],
                [],
                [],
                []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(importedWait, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Shared_resource_import_is_description_exact_and_failure_atomic()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        ImportedResourceState bufferState = new(
            PipelineSync.None,
            ResourceAccess.NoAccess,
            Layout: null,
            QueueType.Graphics);
        BufferDesc bufferDescription = new(
            65_536,
            BufferUsages.CopySource | BufferUsages.CopyDestination | BufferUsages.Shareable);
        using Buffer sourceBuffer = backend.CreateBuffer(device, bufferDescription);
        using ExternalHandle bufferHandle = backend.ExportBuffer(
            sourceBuffer,
            ExternalHandleType.OpaqueWin32);

        Assert.Throws<ArgumentException>(() => backend.ImportBuffer(
            device,
            bufferHandle,
            bufferDescription with { Size = 32_768 },
            bufferState));
        using Buffer importedBuffer = backend.ImportBuffer(
            device,
            bufferHandle,
            bufferDescription,
            bufferState);
        bufferHandle.Dispose();
        Assert.Equal(bufferDescription.Size, importedBuffer.Info.Size);
        Assert.Equal(bufferState.Access, importedBuffer.InitialAccess);
        Assert.Equal(bufferState.QueueType, importedBuffer.InitialQueueType);
        using (Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(bufferDescription.Size, BufferUsages.CopyDestination),
            MemoryType.Readback))
        using (CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1)))
        {
            backend.Begin(context);
            backend.CopyBuffer(
                context,
                new BufferCopy(importedBuffer, 0, readback, 0, bufferDescription.Size));
            using RecordedCommands recorded = backend.End(context);
            RecordedCommands[] commands = [recorded];
            QueueCompletion completion = backend.Submit(
                backend.GetQueue(device, QueueType.Graphics),
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }

        TextureDesc textureDescription = new(
            TextureDimension.Texture2D,
            32,
            16,
            1,
            2,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.CopySource | TextureUsages.CopyDestination |
            TextureUsages.Sampled | TextureUsages.Shareable);
        using Texture sourceTexture = backend.CreateTexture(device, textureDescription);
        using ExternalHandle textureHandle = backend.ExportTexture(
            sourceTexture,
            ExternalHandleType.OpaqueWin32);
        ImportedResourceState textureState = new(
            PipelineSync.None,
            ResourceAccess.NoAccess,
            TextureLayout.Undefined,
            QueueType.Graphics);

        Assert.Throws<ArgumentException>(() => backend.ImportTexture(
            device,
            textureHandle,
            new TextureDesc(
                TextureDimension.Texture2D,
                32,
                16,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.CopySource |
                TextureUsages.CopyDestination |
                TextureUsages.Sampled |
                TextureUsages.Shareable),
            textureState));
        using Texture importedTexture = backend.ImportTexture(
            device,
            textureHandle,
            textureDescription,
            textureState);
        textureHandle.Dispose();
        Assert.Equal(textureDescription.MipLevelCount, importedTexture.Info.MipLevelCount);
        Assert.Equal(textureState.Layout, importedTexture.InitialLayout);
        Assert.Equal(textureState.QueueType, importedTexture.InitialQueueType);
        using TextureSrv importedView = backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                importedTexture,
                new TextureSubresourceRange(0, 2, 0, 1, TextureAspects.Color),
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
    }

    [Fact]
    public void Shared_heap_import_validates_native_metadata()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        HeapDesc description = new(
            131_072,
            0,
            MemoryType.DeviceLocal,
            HeapFlags.Buffers | HeapFlags.Shareable);
        using Heap source = backend.CreateHeap(device, description);
        using ExternalHandle handle = backend.ExportHeap(source, ExternalHandleType.OpaqueWin32);

        Assert.Throws<ArgumentException>(() => backend.ImportHeap(
            device,
            handle,
            description with { Size = 65_536 }));
        using Heap imported = backend.ImportHeap(
            device,
            handle,
            description);
        handle.Dispose();
        Assert.Equal(description.Size, imported.Info.Size);
        Assert.Equal(description.Flags, imported.Info.Flags);
        using Buffer placed = backend.CreatePlacedBuffer(
            device,
            imported,
            0,
            new BufferDesc(65_536, BufferUsages.CopyDestination));
    }
}
