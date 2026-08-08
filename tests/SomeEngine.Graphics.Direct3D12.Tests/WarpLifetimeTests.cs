using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using System.Collections;
using System.Reflection;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpLifetimeTests
{
    [Fact]
    public void Device_disposal_discards_payload_after_its_context_was_disposed()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));

        backend.Begin(context);
        RecordedCommands commands = backend.End(context);
        context.Dispose();
        Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);

        device.Dispose();

        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.Equal(RecordedCommandsStatus.Discarded, commands.Status);
        commands.Dispose();
        Assert.Equal(RecordedCommandsStatus.Disposed, commands.Status);
    }

    [Fact]
    public void Device_disposal_cascades_and_is_concurrent_with_every_WARP_child_family()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 dispatchThread : SV_DispatchThreadID)
            {
            }

            """;
        D3D12TestShaderEntry[] entries =
        [
            new("computeMain", SlangStage.Compute),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_lifetime_matrix",
            source,
            entries);
        using IGraphicsBackend backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        var children = new List<GraphicsObject>();
        D3D12TestShaderProgram? rayShader = null;

        BufferDesc placedDescription = new(
            4_096,
            BufferUsages.ShaderRead | BufferUsages.CopyDestination);
        MemoryRequirements requirements = backend.GetBufferMemoryRequirements(
            device,
            placedDescription);
        Heap heap = Keep(backend.CreateHeap(
            device,
            new HeapDesc(
                requirements.Size,
                requirements.Alignment,
                MemoryType.DeviceLocal,
                requirements.CompatibleHeapFlags)));
        _ = Keep(backend.CreatePlacedBuffer(device, heap, 0, placedDescription));

        Buffer buffer = Keep(backend.CreateBuffer(
            device,
            new BufferDesc(
                4_096,
                BufferUsages.Constant |
                BufferUsages.ShaderRead |
                BufferUsages.ShaderWrite |
                BufferUsages.CopySource |
                BufferUsages.CopyDestination)));
        Buffer counter = Keep(backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderWrite)));
        BufferCbv cbv = Keep(backend.CreateBufferCbv(
            device,
            new BufferCbvDesc(buffer, new BufferRange(0, 256))));
        _ = Keep(backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt)));
        _ = Keep(backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                buffer,
                BufferRange.Whole,
                StructureStride: 16,
                CounterBuffer: counter)));
        _ = Keep(backend.CreateBindlessBufferCbv(
            device,
            new BufferCbvDesc(buffer, new BufferRange(0, 256))));
        _ = Keep(backend.CreateBindlessBufferSrv(
            device,
            new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt)));
        _ = Keep(backend.CreateBindlessBufferUav(
            device,
            new BufferUavDesc(buffer, BufferRange.Whole, Format.R32UInt)));

        Texture color = Keep(backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                16,
                16,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled |
                TextureUsages.Storage |
                TextureUsages.ColorAttachment)));
        TextureSubresourceRange colorRange = new(0, 1, 0, 1, TextureAspects.Color);
        _ = Keep(backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D)));
        _ = Keep(backend.CreateTextureUav(
            device,
            new TextureUavDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D)));
        _ = Keep(backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D)));
        _ = Keep(backend.CreateBindlessTextureSrv(
            device,
            new TextureSrvDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D)));
        _ = Keep(backend.CreateBindlessTextureUav(
            device,
            new TextureUavDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D)));

        Texture depth = Keep(backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                16,
                16,
                1,
                1,
                1,
                1,
                Format.D32Float,
                TextureUsages.DepthStencilAttachment)));
        _ = Keep(backend.CreateDepthStencilView(
            device,
            new DepthStencilViewDesc(
                depth,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
                Format.D32Float,
                TextureViewDimension.Texture2D)));

        SamplerDesc samplerDescription = new(
            FilterType.Nearest,
            FilterType.Nearest,
            FilterType.Nearest,
            AddressType.ClampToEdge,
            AddressType.ClampToEdge,
            AddressType.ClampToEdge);
        _ = Keep(backend.CreateSampler(device, samplerDescription));
        _ = Keep(backend.CreateBindlessSampler(device, samplerDescription));
        _ = Keep(backend.CreateDescriptorTable(device, DescriptorTableType.Resource, 2));
        _ = Keep(backend.CreateDescriptorTable(device, DescriptorTableType.Sampler, 2));
        _ = Keep(backend.CreatePipelineCache(device, default));
        _ = Keep(backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 2)));

        Pipeline computePipeline = Keep(backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0))));
        VariableLayoutReflection globalLayout =
            shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        Assert.NotEqual(VariableLayoutReflection.Null, globalLayout);
        _ = Keep(backend.CreatePersistentParameterBindings(
            device,
            new ParameterBlockBindings(globalLayout, [], [])));

        CommandContext context = Keep(backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1)));
        backend.Begin(context);
        RecordedCommands executable = backend.End(context);
        CommandContext bundleContext = Keep(backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1, Bundle: true)));
        backend.Begin(bundleContext);
        _ = Keep(backend.EndBundle(bundleContext));

        _ = Keep(backend.CreateExternalTimeline(device, 7));
        IndirectArgumentDesc[] indirectArguments = [new(IndirectArgumentType.Draw)];
        _ = Keep(backend.CreateIndirectCommandLayout(
            device,
            new IndirectCommandLayoutDesc(indirectArguments, 16)));

        if (backend.TryGetCapability(device, out SparseResources? sparse) && sparse is not null)
        {
            _ = Keep(backend.CreateReservedBuffer(
                device,
                new BufferDesc(65_536, BufferUsages.CopyDestination)));
        }

        if (backend.TryGetCapability(device, out RayTracing? rayTracing) && rayTracing is not null)
        {
            ulong storageSize = Math.Max(65_536UL, rayTracing.AccelerationStructureAlignment);
            storageSize = checked(
                (storageSize + rayTracing.AccelerationStructureAlignment - 1) /
                rayTracing.AccelerationStructureAlignment *
                rayTracing.AccelerationStructureAlignment);
            Buffer storage = Keep(backend.CreateBuffer(
                device,
                new BufferDesc(storageSize, BufferUsages.AccelerationStructure)));
            AccelerationStructure structure = Keep(backend.CreateAccelerationStructure(
                device,
                storage,
                BufferRange.Whole,
                AccelerationStructureType.BottomLevel));
            _ = Keep(backend.CreateAccelerationStructureSrv(
                device,
                new AccelerationStructureSrvDesc(structure)));
            _ = Keep(backend.CreateBindlessAccelerationStructureSrv(
                device,
                new AccelerationStructureSrvDesc(structure)));

            const string raySource = """
                [shader("raygeneration")]
                void rayGenerationMain()
                {
                }
                """;
            D3D12TestShaderEntry[] rayEntries =
            [
                new("rayGenerationMain", SlangStage.RayGeneration),
            ];
            rayShader = D3D12TestShaderProgram.Compile(
                "rhi_lifetime_ray_table",
                raySource,
                rayEntries);
            EntryPointReflection[] rayGeneration = [rayShader.GetEntryPoint(0)];
            Pipeline rayPipeline = Keep(backend.CreateRayTracingPipeline(
                device,
                new RayTracingPipelineDesc(
                    rayShader.Program,
                    rayGeneration,
                    [],
                    [],
                    [],
                    1,
                    0,
                    8)));
            _ = Keep(backend.CreateRayTracingShaderTable(
                device,
                new RayTracingShaderTableDesc(rayPipeline, 1, 0, 0, 0, 32)));
        }

        var releases = new List<Action>(checked(children.Count * 2 + 2));
        foreach (GraphicsObject child in children)
        {
            releases.Add(child.Dispose);
            releases.Add(child.Dispose);
        }
        releases.Add(device.Dispose);
        releases.Add(device.Dispose);
        Parallel.Invoke(releases.ToArray());

        Assert.Equal(DeviceStatus.Disposed, device.Status);
        foreach (GraphicsObject child in children)
        {
            Assert.True(
                child.IsDisposed,
                $"{child.GetType().Name} remained live after the concurrent Device cascade.");
            child.Dispose();
        }
        Assert.Equal(RecordedCommandsStatus.Discarded, executable.Status);
        executable.Dispose();
        executable.Dispose();
        Assert.Equal(RecordedCommandsStatus.Disposed, executable.Status);
        Assert.Equal(PipelineType.Compute, computePipeline.Type);
        rayShader?.Dispose();

        T Keep<T>(T value)
            where T : GraphicsObject
        {
            children.Add(value);
            return value;
        }
    }

    [Fact]
    public void Surface_or_device_disposal_invalidates_swapchain_images_and_is_idempotent()
    {
        using D3D12TestWindow window = new();
        using IGraphicsBackend backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            32,
            32,
            Format.R8G8B8A8UNorm,
            ColorSpace.Srgb,
            PresentType.Mailbox,
            AllowTearing: false,
            MaximumFrameLatency: 2);
        Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(
                surface,
                2,
                TextureUsages.ColorAttachment,
                config));
        Assert.Equal(
            SwapchainAcquireStatus.Success,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2)),
                out SwapchainImage image));
        Texture imageTexture = image.Texture;

        Parallel.Invoke(
            surface.Dispose,
            surface.Dispose,
            swapchain.Dispose,
            swapchain.Dispose,
            device.Dispose,
            device.Dispose);

        Assert.True(surface.IsDisposed);
        Assert.True(swapchain.IsDisposed);
        Assert.True(imageTexture.IsDisposed);
        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.Equal(SwapchainImageStatus.Invalidated, image.Status);
        Assert.Throws<InvalidOperationException>(() => _ = image.Texture);
        imageTexture.Dispose();
        swapchain.Dispose();
        surface.Dispose();
    }

    [Fact]
    public void External_handle_disposal_is_concurrent_terminal_and_keeps_metadata()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using ExternalTimeline timeline = backend.CreateExternalTimeline(device, 1);
        ExternalHandle handle = backend.ExportTimeline(
            timeline,
            ExternalHandleType.OpaqueWin32);

        Parallel.For(0, 32, _ => handle.Dispose());

        Assert.Equal(ExternalHandleType.OpaqueWin32, handle.Type);
        Assert.Throws<ObjectDisposedException>(() => _ = handle.Value);
        handle.Dispose();
    }

    [Fact]
    public void Replacing_the_owning_root_fully_closes_the_old_runtime_before_the_new_one()
    {
        var oldBackend = new D3D12Backend();
        var oldRoot = new Graphics<D3D12Backend>(oldBackend);
        Device oldDevice = CreateWarpDevice(oldRoot);
        Buffer oldBuffer = oldRoot.CreateBuffer(
            oldDevice,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);

        oldRoot.Dispose();
        oldRoot.Dispose();

        Assert.Equal(DeviceStatus.Disposed, oldDevice.Status);
        Assert.True(oldBuffer.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => oldBackend.TryEnumerateAdapters(
            new AdapterEnumerationOptions(IncludeSoftware: true),
            [],
            out _));

        using var newRoot = new Graphics<D3D12Backend>(new D3D12Backend());
        using Device newDevice = CreateWarpDevice(newRoot);
        using Buffer newBuffer = newRoot.CreateBuffer(
            newDevice,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        Assert.Equal(DeviceStatus.Active, newDevice.Status);
        Assert.NotSame(oldDevice, newDevice);
        Assert.NotSame(oldBuffer, newBuffer);

        static Device CreateWarpDevice(Graphics<D3D12Backend> graphics)
        {
            AdapterEnumerationOptions options = new(
                AdapterPreference.HighPerformance,
                IncludeSoftware: true);
            _ = graphics.TryEnumerateAdapters(options, [], out int count);
            var adapters = new AdapterInfo[count];
            Assert.True(graphics.TryEnumerateAdapters(options, adapters, out int confirmed));
            Assert.Equal(count, confirmed);
            AdapterInfo warp = Assert.Single(adapters, static value => !value.HardwareAccelerated);
            DeviceQueueDesc[] queues = [new(QueueType.Copy)];
            return graphics.CreateDevice(new DeviceDesc(
                warp.Id,
                RetirementType.Automatic,
                queues));
        }
    }

    [Fact]
    public void Heap_and_device_disposal_cascade_once_through_descendants()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        BufferDesc description = new(
            256,
            BufferUsages.ShaderRead | BufferUsages.CopyDestination,
            "placed lifetime buffer");
        MemoryRequirements requirements = backend.GetBufferMemoryRequirements(device, description);
        Heap heap = backend.CreateHeap(device, new HeapDesc(
            requirements.Size,
            requirements.Alignment,
            MemoryType.DeviceLocal,
            requirements.CompatibleHeapFlags));
        Buffer buffer = backend.CreatePlacedBuffer(device, heap, 0, description);
        BufferSrv view = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt));

        Parallel.For(0, 32, _ => heap.Dispose());

        Assert.True(heap.IsDisposed);
        Assert.True(buffer.IsDisposed);
        Assert.True(view.IsDisposed);
        view.Dispose();
        buffer.Dispose();
        heap.Dispose();

        Sampler sampler = backend.CreateSampler(
            device,
            new SamplerDesc(
                FilterType.Nearest,
                FilterType.Nearest,
                FilterType.Nearest,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge));
        device.Dispose();
        device.Dispose();

        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.True(sampler.IsDisposed);
        sampler.Dispose();
    }

    [Fact]
    public void Backend_disposal_cascades_the_complete_device_tree()
    {
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);

        backend.Dispose();
        backend.Dispose();

        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.True(buffer.IsDisposed);
        buffer.Dispose();
        device.Dispose();
    }

    [Fact]
    public void Automatic_submission_retains_native_payload_after_public_owners_dispose()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        byte[] expected = Enumerable.Range(0, 257)
            .Select(static value => unchecked((byte)(value * 43 + 7)))
            .ToArray();
        Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc((ulong)expected.Length, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc((ulong)expected.Length, BufferUsages.CopyDestination),
            MemoryType.Readback);
        BufferRange range = new(0, (ulong)expected.Length);
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, range))
        {
            expected.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }

        CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));
        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, range.Size));
        RecordedCommands recorded = backend.End(context);
        RecordedCommands copied = recorded;
        context.Dispose();
        upload.Dispose();

        RecordedCommands[] commands = [recorded];
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        RecordedCommands[] duplicate = [copied];
        Assert.Throws<InvalidOperationException>(() => backend.Submit(
            queue,
            new QueueSubmitDesc([], [], duplicate, [], [])));
        recorded.Dispose();
        copied.Dispose();

        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
        byte[] actual = new byte[expected.Length];
        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            mapping.Bytes.CopyTo(actual);
        }
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(RetirementType.Manual, false)]
    [InlineData(RetirementType.Automatic, true)]
    public void Retirement_mode_selects_capture_storage_but_both_keep_intrinsic_payload(
        RetirementType retirementType,
        bool expectsAutomaticCapture)
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend, retirementType);
        byte[] expected = Enumerable.Range(0, 257)
            .Select(static value => unchecked((byte)(value * 29 + 11)))
            .ToArray();
        Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc((ulong)expected.Length, BufferUsages.CopySource),
            MemoryType.Upload);
        Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc((ulong)expected.Length, BufferUsages.CopyDestination),
            MemoryType.Readback);
        BufferRange range = new(0, (ulong)expected.Length);
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, range))
        {
            expected.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }

        CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));
        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, range.Size));
        RecordedCommands recorded = backend.End(context);

        IList slots = (IList)GetRequiredField(context, "_slots").GetValue(context)!;
        Assert.Single(slots);
        object slot = slots[0]!;
        object? captureArena = GetRequiredField(slot, "_automaticCaptures").GetValue(slot);
        Assert.Equal(expectsAutomaticCapture, captureArena is not null);
        if (captureArena is not null)
        {
            int objectCount = (int)GetRequiredProperty(captureArena, "ObjectCount")
                .GetValue(captureArena)!;
            Assert.True(objectCount >= 2);
        }

        context.Dispose();
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Copy),
            new QueueSubmitDesc([], [], [recorded], [], []));
        recorded.Dispose();

        if (retirementType == RetirementType.Automatic)
            upload.Dispose();

        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
        byte[] actual = new byte[expected.Length];
        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            mapping.Bytes.CopyTo(actual);
        }
        Assert.Equal(expected, actual);

        upload.Dispose();
        readback.Dispose();
        context.Dispose();
    }

    [Theory]
    [InlineData(RetirementType.Manual)]
    [InlineData(RetirementType.Automatic)]
    public void End_releases_CommandContext_encoding_state_references(
        RetirementType retirementType)
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend, retirementType);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                64,
                BufferUsages.Vertex | BufferUsages.Index | BufferUsages.Predication),
            MemoryType.Upload);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));

        backend.Begin(context);
        backend.SetVertexBuffers(
            context,
            0,
            [new VertexBufferBinding(buffer, 0, 16, 64)]);
        backend.SetIndexBuffer(
            context,
            new IndexBufferBinding(buffer, 0, 64, IndexType.UInt16));
        backend.SetPredication(context, buffer, 0);
        using RecordedCommands commands = backend.End(context);

        IDictionary vertexBuffers =
            (IDictionary)GetRequiredField(context, "_vertexBuffers").GetValue(context)!;
        Assert.Empty(vertexBuffers);
        Assert.Equal(
            default,
            (IndexBufferBinding)GetRequiredField(context, "_indexBuffer").GetValue(context)!);
        Assert.Null(GetRequiredField(context, "_predication").GetValue(context));
        Assert.Null(GetRequiredField(context, "_pipeline").GetValue(context));
        Assert.Null(GetRequiredField(context, "_shadingRateImage").GetValue(context));
        Assert.Null(GetRequiredField(context, "_workGraphProgram").GetValue(context));
    }

    [Fact]
    public void Reused_command_slot_does_not_resurrect_stale_recorded_command_copies()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        var commands = new RecordedCommands[1];

        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 64));
        RecordedCommands first = backend.End(context);
        RecordedCommands staleCopy = first;
        commands[0] = first;
        QueueCompletion firstCompletion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        first.Dispose();
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(firstCompletion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);

        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 64));
        RecordedCommands second = backend.End(context);

        Assert.Same(device, staleCopy.Device);
        Assert.Same(queue, staleCopy.Queue);
        Assert.Throws<InvalidOperationException>(() => _ = staleCopy.Status);
        staleCopy.Dispose();

        commands[0] = second;
        QueueCompletion secondCompletion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        second.Dispose();
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(secondCompletion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    private static FieldInfo GetRequiredField(object instance, string name) =>
        instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"{instance.GetType().FullName} has no non-public field named {name}.");

    private static PropertyInfo GetRequiredProperty(object instance, string name) =>
        instance.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"{instance.GetType().FullName} has no non-public property named {name}.");
}
