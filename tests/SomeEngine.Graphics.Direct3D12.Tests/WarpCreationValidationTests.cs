using System.Numerics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpCreationValidationTests
{
    [Fact]
    public void Backend_rejects_objects_created_by_another_backend_instance()
    {
        using var owner = new D3D12Backend();
        using var foreign = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(owner);
        using Buffer buffer = owner.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);

        Assert.Throws<ArgumentException>(() =>
        {
            using MappedBuffer _ = foreign.Map(buffer, MapType.Write, BufferRange.Whole);
        });
    }

    [Fact]
    public void Device_scoped_creation_rejects_resources_from_another_device()
    {
        using var backend = new D3D12Backend();
        using Device first = D3D12TestSupport.CreateWarpDevice(backend);
        using Device second = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            first,
            new BufferDesc(256, BufferUsages.ShaderRead));

        Assert.Throws<ArgumentException>(() =>
            backend.CreateBufferSrv(
                second,
                new BufferSrvDesc(buffer, BufferRange.Whole)));
    }

    [Fact]
    public void Unrequested_feature_capabilities_are_not_enabled()
    {
        using var backend = new D3D12Backend();
        AdapterInfo warp = D3D12TestSupport.SelectWarp(backend);
        using Device device = backend.CreateDevice(new DeviceDesc(
            warp.Id,
            [new DeviceQueueDesc(QueueType.Graphics)]));

        Assert.False(backend.TryGetCapability<Presentation>(device, out _));
        Assert.False(backend.TryGetCapability<SparseResources>(device, out _));
        Assert.False(backend.TryGetCapability<SamplerFeedback>(device, out _));
        Assert.False(backend.TryGetCapability<Residency>(device, out _));
        Assert.False(backend.TryGetCapability<RayTracing>(device, out _));
        Assert.False(backend.TryGetCapability<MeshShaders>(device, out _));
        Assert.False(backend.TryGetCapability<VariableRateShading>(device, out _));
        Assert.False(backend.TryGetCapability<WorkGraphs>(device, out _));
        Assert.False(backend.TryGetCapability<IndirectCommands>(device, out _));
        Assert.False(backend.TryGetCapability<CalibratedTimestamps>(device, out _));
        Assert.False(backend.TryGetCapability<LinkedAdapters>(device, out _));
        Assert.False(backend.TryGetCapability<ExternalResources>(device, out _));
        Assert.False(backend.TryGetCapability<ExternalTimelines>(device, out _));
        Assert.True(backend.TryGetCapability<D3D12NativeAccess>(device, out _));
        Assert.True(backend.TryGetCapability<D3D12Diagnostics>(device, out _));
    }

    [Fact]
    public void Unavailable_optional_feature_does_not_disable_the_device()
    {
        using var backend = new D3D12Backend();
        AdapterInfo warp = D3D12TestSupport.SelectWarp(backend);
        using Device device = backend.CreateDevice(new DeviceDesc(
            warp.Id,
            [new DeviceQueueDesc(QueueType.Graphics)],
            optionalFeatures: DeviceFeatures.LinkedAdapters));

        Assert.False(backend.TryGetCapability<LinkedAdapters>(device, out _));
        Assert.Equal(DeviceStatus.Active, device.Status);
    }

    [Fact]
    public void Presentation_must_be_enabled_before_swapchain_creation()
    {
        using D3D12TestWindow window = new();
        using var backend = new D3D12Backend();
        AdapterInfo warp = D3D12TestSupport.SelectWarp(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        DeviceQueueDesc[] copyQueues = [new(QueueType.Copy)];
        DeviceQueueDesc[] graphicsQueues = [new(QueueType.Graphics)];
        SwapchainConfig config = new(
            32,
            32,
            Format.R8G8B8A8UNorm,
            ColorSpace.Srgb,
            PresentType.Mailbox,
            false,
            2);
        using (Device disabled = backend.CreateDevice(new DeviceDesc(
            warp.Id,
            copyQueues,
            optionalFeatures: DeviceFeatures.Presentation)))
        {
            Assert.False(backend.TryGetCapability<Presentation>(disabled, out _));
            Assert.Throws<NotSupportedException>(() =>
                backend.CreateSwapchain(
                    disabled,
                    new SwapchainDesc(
                        surface,
                        2,
                        TextureUsages.ColorAttachment,
                        config)));
            Assert.Equal(DeviceStatus.Active, disabled.Status);
        }

        GraphicsException missing = Assert.Throws<GraphicsException>(() =>
            backend.CreateDevice(new DeviceDesc(
                warp.Id,
                copyQueues,
                requiredFeatures: DeviceFeatures.Presentation)));
        Assert.Equal(GraphicsError.NativeFailure, missing.Error);
        Assert.Null(missing.NativeCode);

        using Device enabled = backend.CreateDevice(new DeviceDesc(
            warp.Id,
            graphicsQueues,
            optionalFeatures: DeviceFeatures.Presentation));
        Assert.True(backend.TryGetCapability<Presentation>(enabled, out Presentation? presentation));
        Assert.NotNull(presentation);
        Assert.Same(enabled, presentation.Device);
        using Swapchain swapchain = backend.CreateSwapchain(
            enabled,
            new SwapchainDesc(
                surface,
                2,
                TextureUsages.ColorAttachment,
                config));
        Assert.Equal(ReconfigureStatus.Success, backend.Reconfigure(swapchain, config));
    }

    [Fact]
    public void Missing_required_feature_is_a_retryable_graphics_failure()
    {
        using var backend = new D3D12Backend();
        AdapterInfo warp = D3D12TestSupport.SelectWarp(backend);
        DeviceQueueDesc[] queues = [new(QueueType.Graphics)];

        GraphicsException failure = Assert.Throws<GraphicsException>(() =>
            backend.CreateDevice(new DeviceDesc(
                warp.Id,
                queues,
                requiredFeatures: DeviceFeatures.LinkedAdapters)));

        Assert.Equal(GraphicsError.NativeFailure, failure.Error);
        Assert.Null(failure.NativeCode);

        using Device device = backend.CreateDevice(new DeviceDesc(
            warp.Id,
            queues));
        Assert.Equal(DeviceStatus.Active, device.Status);
    }

    [Fact]
    public void Explicit_WARP_selection_preserves_the_enumerated_adapter_identity()
    {
        using var backend = new D3D12Backend();
        AdapterInfo selected = D3D12TestSupport.SelectWarp(backend);
        using Device device = backend.CreateDevice(new DeviceDesc(
            selected.Id,
            [new DeviceQueueDesc(QueueType.Graphics)]));

        Assert.Equal(selected, device.Adapter);
        Assert.False(device.Adapter.HardwareAccelerated);
    }

    [Fact]
    public void Device_description_rejects_malformed_queue_topology_before_native_creation()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        AdapterInfo adapter = D3D12TestSupport.SelectWarp(backend);

        Assert.Throws<ArgumentException>(() => backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [])));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics, Count: 0)])));
        Assert.Throws<ArgumentException>(() => backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Graphics)])));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics, Priority: float.NaN)])));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc((QueueType)byte.MaxValue)])));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics, NodeIndex: 1)],
            enabledNodeMask: 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics)],
            requiredFeatures: (DeviceFeatures)(1UL << 63))));

        using Device valid = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics)]));
        Assert.Equal(QueueType.Graphics, backend.GetQueue(valid, QueueType.Graphics).Type);
    }

    [Fact]
    public void Buffer_and_heap_descriptions_are_rejected_before_native_creation()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.CreateBuffer(device, new BufferDesc(0, BufferUsages.CopySource)));
        Assert.Throws<ArgumentException>(() =>
            backend.CreateBuffer(
                device,
                new BufferDesc(256, BufferUsages.ShaderWrite),
                MemoryType.Upload));
        Assert.Throws<ArgumentException>(() =>
            backend.CreateBuffer(
                device,
                new BufferDesc(256, BufferUsages.CopySource),
                MemoryType.Readback));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.CreateHeap(
                device,
                new HeapDesc(0, 0, MemoryType.DeviceLocal, HeapFlags.Buffers)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.CreateHeap(
                device,
                new HeapDesc(65_536, 123, MemoryType.DeviceLocal, HeapFlags.Buffers)));
        Assert.Throws<ArgumentException>(() =>
            backend.CreateHeap(
                device,
                new HeapDesc(
                    65_536,
                    0,
                    MemoryType.DeviceLocal,
                    HeapFlags.Buffers,
                    CreationNodeMask: 0,
                    VisibleNodeMask: 1)));
    }

    [Fact]
    public void Texture_descriptions_enforce_dimensions_formats_and_usage()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                0,
                64,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled)));
        Assert.Throws<ArgumentException>(() => backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture1D,
                64,
                2,
                1,
                1,
                1,
                1,
                Format.R8UNorm,
                TextureUsages.Sampled)));
        Assert.Throws<ArgumentException>(() => backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture3D,
                16,
                16,
                16,
                1,
                2,
                1,
                Format.R8UNorm,
                TextureUsages.Sampled)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                8,
                8,
                1,
                5,
                1,
                1,
                Format.R8UNorm,
                TextureUsages.Sampled)));
        Assert.Throws<ArgumentException>(() => backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                2,
                1,
                4,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment)));
        Assert.Throws<ArgumentException>(() => backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.D32Float,
                TextureUsages.ColorAttachment)));
        Assert.Throws<ArgumentException>(() => backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.DepthStencilAttachment)));
        Assert.Throws<ArgumentException>(() => backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.BC1UNorm,
                TextureUsages.Storage)));
        Assert.Throws<ArgumentException>(() => backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled,
                [Format.R32Float])));
        Assert.Throws<ArgumentException>(() => backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled,
                [Format.R8G8B8A8UNormSrgb, Format.R8G8B8A8UNormSrgb])));
    }

    [Fact]
    public void Ordinary_texture_paths_reject_the_sampler_feedback_subtype()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        TextureDesc ordinary = new(
            TextureDimension.Texture2D,
            64,
            64,
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.CopySource |
            TextureUsages.CopyDestination |
            TextureUsages.Sampled |
            TextureUsages.Storage);
        BufferTextureCopy copy = new(
            null!,
            0,
            0,
            0,
            null!,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            64,
            64,
            1);

        Assert.Throws<ArgumentException>(() =>
            backend.GetTextureMemoryRequirements(device, CreateFeedbackTextureDescription()));
        Assert.Throws<ArgumentException>(() =>
            backend.GetTextureCopyFootprint(
                device,
                CreateFeedbackTextureDescription(),
                copy));
        Assert.Throws<ArgumentException>(() =>
            backend.CreateTexture(device, CreateFeedbackTextureDescription()));

        MemoryRequirements requirements = backend.GetTextureMemoryRequirements(device, ordinary);
        using Heap heap = backend.CreateHeap(
            device,
            new HeapDesc(
                requirements.Size,
                0,
                MemoryType.DeviceLocal,
                HeapFlags.Textures));
        Assert.Throws<ArgumentException>(() =>
            backend.CreatePlacedTexture(
                device,
                heap,
                0,
                CreateFeedbackTextureDescription()));

        Assert.True(backend.TryGetCapability(device, out SparseResources? sparse));
        Assert.NotNull(sparse);
        Assert.Throws<ArgumentException>(() =>
            backend.CreateReservedTexture(device, CreateFeedbackTextureDescription()));

        using Texture committed = backend.CreateTexture(device, ordinary);
        using Texture placed = backend.CreatePlacedTexture(device, heap, 0, ordinary);
        using Texture reserved = backend.CreateReservedTexture(device, ordinary);
        Assert.Equal(TextureUsages.Storage, committed.Info.Usages & TextureUsages.Storage);
        Assert.Equal(TextureUsages.Storage, placed.Info.Usages & TextureUsages.Storage);
        Assert.Equal(TextureUsages.Storage, reserved.Info.Usages & TextureUsages.Storage);
    }

    private static TextureDesc CreateFeedbackTextureDescription() => new(
        TextureDimension.Texture2D,
        64,
        64,
        1,
        1,
        1,
        1,
        Format.R8G8B8A8UNorm,
        TextureUsages.Storage | TextureUsages.SamplerFeedback);

    [Fact]
    public void Placed_resources_validate_heap_class_alignment_and_range()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        BufferDesc bufferDesc = new(4_096, BufferUsages.CopyDestination);
        MemoryRequirements bufferRequirements =
            backend.GetBufferMemoryRequirements(device, bufferDesc);
        using Heap bufferHeap = backend.CreateHeap(
            device,
            new HeapDesc(
                checked(bufferRequirements.Size * 2),
                0,
                MemoryType.DeviceLocal,
                HeapFlags.Buffers));
        using Heap textureHeap = backend.CreateHeap(
            device,
            new HeapDesc(
                checked(bufferRequirements.Size * 2),
                0,
                MemoryType.DeviceLocal,
                HeapFlags.Textures));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.CreatePlacedBuffer(device, bufferHeap, 1, bufferDesc));
        Assert.Throws<ArgumentException>(() =>
            backend.CreatePlacedBuffer(device, textureHeap, 0, bufferDesc));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.CreatePlacedBuffer(
                device,
                bufferHeap,
                checked(bufferRequirements.Size * 2),
                bufferDesc));

        using Buffer placed = backend.CreatePlacedBuffer(device, bufferHeap, 0, bufferDesc);
        Assert.Same(bufferHeap, placed.Heap);
        Assert.Equal(0UL, placed.Info.AllocationOffset);
    }

    [Fact]
    public void Buffer_views_validate_usage_shape_alignment_and_counters()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                8_192,
                BufferUsages.Constant | BufferUsages.ShaderRead | BufferUsages.ShaderWrite));
        using Buffer counter = backend.CreateBuffer(
            device,
            new BufferDesc(8_192, BufferUsages.ShaderWrite));

        Assert.Throws<ArgumentException>(() => backend.CreateBufferCbv(
            device,
            new BufferCbvDesc(buffer, new BufferRange(1, 256))));
        Assert.Throws<ArgumentException>(() => backend.CreateBufferCbv(
            device,
            new BufferCbvDesc(buffer, new BufferRange(0, 128))));
        Assert.Throws<ArgumentException>(() => backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(
                buffer,
                new BufferRange(0, 256),
                Format.R32Float,
                StructureStride: 16)));
        Assert.Throws<ArgumentException>(() => backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, new BufferRange(1, 255), Format.R32Float)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, new BufferRange(0, 4_100), StructureStride: 2_052)));
        Assert.Throws<ArgumentException>(() => backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                buffer,
                new BufferRange(0, 256),
                Format: Format.R32UInt,
                CounterBuffer: counter)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                buffer,
                new BufferRange(0, 256),
                StructureStride: 16,
                CounterBuffer: counter,
                CounterOffset: 4)));

        using BufferCbv cbv = backend.CreateBufferCbv(
            device,
            new BufferCbvDesc(buffer, new BufferRange(0, 256)));
        using BufferSrv srv = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, new BufferRange(0, 256), Format.R32Float));
        using BufferUav uav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                buffer,
                new BufferRange(0, 256),
                StructureStride: 16,
                CounterBuffer: counter,
                CounterOffset: 0));
    }

    [Fact]
    public void Texture_views_validate_declared_format_subresources_and_native_shape()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Format[] permitted = [Format.R8G8B8A8UNorm, Format.R8G8B8A8UNormSrgb];
        using Texture color = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
                permitted));
        TextureSubresourceRange colorRange = new(0, 1, 0, 1, TextureAspects.Color);

        Assert.Throws<ArgumentException>(() => backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                color,
                colorRange,
                Format.R16Float,
                TextureViewDimension.Texture2D)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                color,
                colorRange with { FirstMipLevel = 1 },
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D)));
        Assert.Throws<ArgumentException>(() => backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Cube)));
        Assert.Throws<ArgumentException>(() => backend.CreateTextureUav(
            device,
            new TextureUavDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNormSrgb,
                TextureViewDimension.Texture2D)));

        using TextureSrv srv = backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNormSrgb,
                TextureViewDimension.Texture2D));
        using TextureUav uav = backend.CreateTextureUav(
            device,
            new TextureUavDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        using ColorAttachmentView rtv = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));

        using Texture depth = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.D24UNormS8UInt,
                TextureUsages.Sampled | TextureUsages.DepthStencilAttachment));
        TextureSubresourceRange fullDepth = new(
            0,
            1,
            0,
            1,
            TextureAspects.Depth | TextureAspects.Stencil);
        Assert.Throws<ArgumentException>(() => backend.CreateDepthStencilView(
            device,
            new DepthStencilViewDesc(
                depth,
                fullDepth with { Aspects = TextureAspects.Depth },
                Format.D24UNormS8UInt,
                TextureViewDimension.Texture2D)));
        using DepthStencilView dsv = backend.CreateDepthStencilView(
            device,
            new DepthStencilViewDesc(
                depth,
                fullDepth,
                Format.D24UNormS8UInt,
                TextureViewDimension.Texture2D));
        using TextureSrv depthSrv = backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                depth,
                fullDepth with { Aspects = TextureAspects.Depth },
                Format.D24UNormS8UInt,
                TextureViewDimension.Texture2D));
    }

    [Fact]
    public void Invalid_sampler_creation_does_not_consume_descriptor_storage()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        SamplerDesc invalid = new(
            FilterType.Linear,
            FilterType.Linear,
            FilterType.Linear,
            AddressType.Repeat,
            AddressType.Repeat,
            AddressType.Repeat,
            MaximumAnisotropy: 17);

        for (int index = 0; index < 2_048; index++)
            Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateSampler(device, invalid));

        using Sampler sampler = backend.CreateSampler(
            device,
            new SamplerDesc(
                FilterType.Linear,
                FilterType.Linear,
                FilterType.Linear,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge));
        Assert.Equal(1u, sampler.Description.MaximumAnisotropy);
    }
}
