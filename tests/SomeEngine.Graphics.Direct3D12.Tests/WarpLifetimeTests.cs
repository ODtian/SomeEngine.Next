using System.Reflection;
using Silk.NET.Direct3D12;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using System.Collections;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class WarpLifetimeTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public async Task Validated_graphics_root_cross_thread_dispose_joins_the_single_receiver_release()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int lifetimeMessages = 0;
        ValidationLayer? validation = null;
        var sink = new DelegateValidationMessageSink(message =>
        {
            if (message.Area != "Lifetime")
                return;

            Interlocked.Increment(ref lifetimeMessages);
            validation!.Dispose();
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            events.Enqueue("receiver callback completed");
        });
        var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        validation = new ValidationLayer(
            backend,
            new ValidationOptions(sink, ReportLiveObjectsOnDispose: true));
        Device device = CreateWarpDevice(validation);
        Task disposal = Task.CompletedTask;
        Task contender = Task.CompletedTask;

        try
        {
            disposal = Task.Run(() =>
            {
                validation.Dispose();
                events.Enqueue("first disposal completed");
            });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<ObjectDisposedException>(() => validation.TryEnumerateAdapters(
                new AdapterEnumerationOptions(IncludeSoftware: true),
                [],
                out _));

            using var contenderStarted = new ManualResetEventSlim();
            contender = Task.Run(() =>
            {
                contenderStarted.Set();
                validation.Dispose();
                Assert.Equal(DeviceStatus.Disposed, device.Status);
                Assert.Throws<ObjectDisposedException>(() => backend.TryEnumerateAdapters(
                    new AdapterEnumerationOptions(IncludeSoftware: true), [], out _));
                events.Enqueue("second disposal completed");
            });
            Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            await Task.WhenAll(disposal, contender).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("receiver callback completed", events.First());
            Assert.Contains("first disposal completed", events);
            Assert.Contains("second disposal completed", events);

            Assert.Equal(1, Volatile.Read(ref lifetimeMessages));
            Assert.Equal(DeviceStatus.Disposed, device.Status);
            Assert.Throws<ObjectDisposedException>(() => backend.TryEnumerateAdapters(
                new AdapterEnumerationOptions(IncludeSoftware: true),
                [],
                out _));
        }
        finally
        {
            release.Set();
            try { await Task.WhenAll(disposal, contender).WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { validation.Dispose(); }
        }
    }

    [Fact]
    public async Task Validation_receiver_contenders_join_and_sink_failure_is_observational()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sink = new BlockingThrowingSink(entered, release, () => events.Enqueue("receiver callback completed"));
        var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        var validation = new ValidationLayer(
            backend,
            new ValidationOptions(sink, ReportLiveObjectsOnDispose: true));
        Device device = D3D12TestSupport.CreateWarpDevice(validation);
        Task disposal = Task.CompletedTask;
        Task contender = Task.CompletedTask;

        try
        {
            disposal = Task.Run(() =>
            {
                validation.Dispose();
                events.Enqueue("first disposal completed");
            });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            using var contenderStarted = new ManualResetEventSlim();
            contender = Task.Run(() =>
            {
                contenderStarted.Set();
                validation.Dispose();
                Assert.Equal(DeviceStatus.Disposed, device.Status);
                Assert.Throws<ObjectDisposedException>(() => backend.TryEnumerateAdapters(
                    new AdapterEnumerationOptions(IncludeSoftware: true), [], out _));
                events.Enqueue("second disposal completed");
            });
            Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            await Task.WhenAll(disposal, contender).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("receiver callback completed", events.First());
            Assert.Contains("first disposal completed", events);
            Assert.Contains("second disposal completed", events);

            Assert.Equal(1, sink.ReportCount);
            Assert.Equal(DeviceStatus.Disposed, device.Status);
            Assert.Throws<ObjectDisposedException>(() => backend.TryEnumerateAdapters(
                new AdapterEnumerationOptions(IncludeSoftware: true),
                [],
                out _));

            validation.Dispose();
            for (int index = 0; index < 1_024; index++)
                validation.Dispose();
        }
        finally
        {
            release.Set();
            try { await Task.WhenAll(disposal, contender).WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { validation.Dispose(); }
        }
    }



    [Fact]
    public async Task Backend_cross_thread_dispose_joins_while_real_queue_gate_is_held()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        using var gateHeld = new ManualResetEventSlim();
        using var invalidationObserved = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        using var contenderStarted = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            D3D12CommandQueueLock held = backend.LockCommandQueue(queue);
            try
            {
                gateHeld.Set();
                Assert.True(SpinWait.SpinUntil(
                    () => device.Status == DeviceStatus.Disposed,
                    TimeSpan.FromSeconds(5)));
                WaitUntilQueueLockInvalidated(held);
                AssertQueueLockPointerUnavailable(held);
                invalidationObserved.Set();
                Assert.True(releaseGate.Wait(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                lock (events)
                {
                    held.Dispose();
                    events.Enqueue("queue lock released");
                }
            }
        });
        Task disposal = Task.CompletedTask;
        Task contender = Task.CompletedTask;
        try
        {
            Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(5)));
            disposal = Task.Run(() =>
            {
                backend.Dispose();
                lock (events) events.Enqueue("first disposal completed");
            });
            Assert.True(SpinWait.SpinUntil(
                () => device.Status == DeviceStatus.Disposed,
                TimeSpan.FromSeconds(5)));
            Assert.True(invalidationObserved.Wait(TimeSpan.FromSeconds(5)));
            contender = Task.Run(() =>
            {
                contenderStarted.Set();
                backend.Dispose();
                lock (events)
                {
                    Assert.Contains("queue lock released", events);
                    events.Enqueue("second disposal completed");
                }
            });
            Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
            releaseGate.Set();
            await Task.WhenAll(holder, disposal, contender).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("queue lock released", events.First());
            Assert.Contains("first disposal completed", events);
            Assert.Contains("second disposal completed", events);
        }
        finally
        {
            releaseGate.Set();
            try { await Task.WhenAll(holder, disposal, contender).WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { backend.Dispose(); }
        }
    }















    [Fact]
    public void Backend_teardown_quiesces_submitted_payload_before_releasing_native_roots()
    {
        var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context, default);
        RecordedCommands commands = backend.End(context);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        _ = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));

        backend.Dispose();

        Assert.Equal(RecordedCommandsStatus.Completed, commands.Status);
        Assert.Null(diagnostics!.TeardownFailure);
        commands.Dispose();
        context.Dispose();
        device.Dispose();
    }



    [Fact]
    public void Structured_uav_retains_both_native_resources_without_owning_their_wrappers_DoesNotThrow()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Buffer data = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderWrite));
        Buffer counter = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderWrite));
        BufferUav first = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                data,
                BufferRange.Whole,
                StructureStride: 16,
                CounterBuffer: counter));

        counter.Dispose();
        first.Dispose();

        Buffer secondCounter = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderWrite));
        BufferUav second = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                data,
                BufferRange.Whole,
                StructureStride: 16,
                CounterBuffer: secondCounter));
        data.Dispose();
        second.Dispose();
        secondCounter.Dispose();
    }

    [Fact]
    public void Structured_uav_with_identical_resource_and_counter_retains_one_native_payload()
    {
        var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Buffer parent = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderWrite));
        BufferUav ordinary = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                parent,
                BufferRange.Whole,
                StructureStride: 16,
                CounterBuffer: parent));

        ordinary.Dispose();

        BufferUav tableView = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                parent,
                BufferRange.Whole,
                StructureStride: 16,
                CounterBuffer: parent));
        using DescriptorTable table = backend.CreateSingleDescriptorTable(
            device,
            new DescriptorSlotDesc(
                ResourceBindingType.BufferUav,
                StructureStride: 16,
                HasCounter: true),
            ResourceBinding.WritableBuffer(tableView),
            out _);
        parent.Dispose();
        tableView.Dispose();

        device.Dispose();
        backend.Dispose();
        Assert.Null(diagnostics!.TeardownFailure);
    }




    [Fact]
    public async Task Validated_queue_lock_is_invalidated_before_parent_release()
    {
        var direct = D3D12GraphicsBackend.Create();
        var backend = new ValidationLayer(direct);
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        using var held = new ManualResetEventSlim();
        using var invalidated = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var holder = Task.Run(() =>
        {
            D3D12CommandQueueLock value = backend.LockCommandQueue(queue);
            try
            {
                held.Set();
                Assert.True(SpinWait.SpinUntil(
                    () => device.Status == DeviceStatus.Disposed,
                    TimeSpan.FromSeconds(5)));
                WaitUntilQueueLockInvalidated(value);
                AssertQueueLockPointerUnavailable(value);
                invalidated.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                lock (events)
                {
                    value.Dispose();
                    events.Enqueue("queue lock released");
                }
            }
        });
        Task parent = Task.CompletedTask;
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            parent = Task.Run(() =>
            {
                backend.Dispose();
                lock (events)
                {
                    Assert.Contains("queue lock released", events);
                    events.Enqueue("parent disposal completed");
                }
            });
            Assert.True(invalidated.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            await Task.WhenAll(holder, parent).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["queue lock released", "parent disposal completed"], events);
            Assert.Equal(DeviceStatus.Disposed, device.Status);
        }
        finally
        {
            release.Set();
            try { await Task.WhenAll(holder, parent).WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { backend.Dispose(); }
        }
    }













    private sealed class BlockingThrowingSink : IValidationMessageSink
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private readonly Action _completed;

        internal BlockingThrowingSink(
            ManualResetEventSlim entered,
            ManualResetEventSlim release,
            Action completed)
        {
            _entered = entered;
            _release = release;
            _completed = completed;
        }

        internal int ReportCount { get; private set; }

        public void Report(in ValidationMessage message)
        {
            if (message.Area != "Lifetime")
                return;

            ReportCount++;
            _entered.Set();
            Assert.True(_release.Wait(TimeSpan.FromSeconds(10)));
            _completed();
            throw new InvalidOperationException("The diagnostic sink failed during teardown.");
        }
    }

    private static Device CreateWarpDevice(IGraphicsBackend graphics)
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
            queues));
    }
    [Fact]
    public void Device_disposal_discards_payload_after_its_context_was_disposed()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));

        backend.Begin(context, default);
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
        D3D12TestEntry[] entries =
        [
            new("computeMain", SlangStage.Compute),
        ];
        D3D12TestShader shader = shaderCompiler.Compile(
            "rhi_lifetime_matrix",
            source,
            entries);
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        var children = new List<GraphicsObject>();
        D3D12TestShader? rayShader = null;

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
        BufferSrv bufferSrv = Keep(backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt)));
        _ = Keep(backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                buffer,
                BufferRange.Whole,
                StructureStride: 16,
                CounterBuffer: counter)));

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
        TextureSrv textureSrv = Keep(backend.CreateTextureSrv(
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
        Sampler sampler = Keep(backend.CreateSampler(device, samplerDescription));
        DescriptorTable resourceDescriptors = Keep(backend.CreateDescriptorTable(
            device,
            [ResourceBindingType.BufferSrv, ResourceBindingType.TextureSrv]));
        backend.WriteDescriptor(resourceDescriptors, 0, ResourceBinding.ReadOnlyBuffer(bufferSrv));
        backend.WriteDescriptor(resourceDescriptors, 1, ResourceBinding.SampledTexture(textureSrv));
        DescriptorTable samplerDescriptors = Keep(backend.CreateDescriptorTable(
            device,
            [ResourceBindingType.Sampler, ResourceBindingType.Sampler]));
        backend.WriteDescriptor(samplerDescriptors, 0, ResourceBinding.SampledWith(sampler));
        backend.WriteDescriptor(samplerDescriptors, 1, ResourceBinding.SampledWith(sampler));
        _ = Keep(backend.CreatePipelineCache(device, default));
        _ = Keep(backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 2)));

        Pipeline computePipeline = Keep(backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0))));
        VariableLayoutReflection globalLayout =
            shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        Assert.NotEqual(VariableLayoutReflection.Null, globalLayout);
        _ = Keep(backend.CreatePersistentParameterBindings(
            device,
            computePipeline,
            new ParameterBlockBindings(globalLayout, [], [])));

        CommandContext context = Keep(backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1)));
        backend.Begin(context, default);
        RecordedCommands executable = backend.End(context);
        CommandContext bundleContext = Keep(backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1, Bundle: true)));
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
            AccelerationStructureSrv structureSrv = Keep(backend.CreateAccelerationStructureSrv(
                device,
                new AccelerationStructureSrvDesc(structure)));
            DescriptorTable structureDescriptors = Keep(backend.CreateDescriptorTable(
                device,
                [ResourceBindingType.AccelerationStructure]));
            backend.WriteDescriptor(
                structureDescriptors,
                0,
                ResourceBinding.AccelerationStructure(structureSrv));

            const string raySource = """
                [shader("raygeneration")]
                void rayGenerationMain()
                {
                }
                """;
            D3D12TestEntry[] rayEntries =
            [
                new("rayGenerationMain", SlangStage.RayGeneration),
            ];
            rayShader = shaderCompiler.Compile(
                "rhi_lifetime_ray_table",
                raySource,
                rayEntries);
            EntryPointReflection[] rayGeneration = [rayShader.GetEntryPoint(0)];
            Pipeline rayPipeline = Keep(backend.CreateRayTracingPipeline(
                device,
                new RayTracingPipelineDesc(rayShader.SlangCode, rayShader.Reflection, rayShader.TargetCode,
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
            child.Dispose();
        }
        Assert.Equal(RecordedCommandsStatus.Discarded, executable.Status);
        executable.Dispose();
        executable.Dispose();
        Assert.Equal(RecordedCommandsStatus.Disposed, executable.Status);
        Assert.Equal(PipelineType.Compute, computePipeline.Type);

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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        var oldBackend = D3D12GraphicsBackend.Create();
        Device oldDevice = CreateWarpDevice(oldBackend);
        Buffer oldBuffer = oldBackend.CreateBuffer(
            oldDevice,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);

        oldBackend.Dispose();
        oldBackend.Dispose();

        Assert.Equal(DeviceStatus.Disposed, oldDevice.Status);
        Assert.Throws<ObjectDisposedException>(() => oldBackend.TryEnumerateAdapters(
            new AdapterEnumerationOptions(IncludeSoftware: true),
            [],
            out _));

        using var newBackend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device newDevice = CreateWarpDevice(newBackend);
        using Buffer newBuffer = newBackend.CreateBuffer(
            newDevice,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        Assert.Equal(DeviceStatus.Active, newDevice.Status);
        Assert.NotSame(oldDevice, newDevice);
        Assert.NotSame(oldBuffer, newBuffer);
        newBackend.Dispose();

        static Device CreateWarpDevice(IGraphicsBackend graphics)
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
                    queues));
        }
    }

    [Fact]
    public void Heap_and_device_disposal_cascade_once_through_descendants()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        sampler.Dispose();
    }

    [Fact]
    public void View_retains_resource_native_state_exactly_until_view_disposal_DoesNotThrow()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderRead),
            MemoryType.DeviceLocal);

        BufferSrv view = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt));

        view.Dispose();
        view.Dispose();
    }


    [Fact]
    public void Backend_disposal_cascades_the_complete_device_tree()
    {
        var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);

        backend.Dispose();
        backend.Dispose();

        Assert.Equal(DeviceStatus.Disposed, device.Status);
        buffer.Dispose();
        device.Dispose();
    }

    [Fact]
    public void Automatic_submission_retains_native_payload_after_public_owners_dispose()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context, default);
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



    [Fact]
    public void Reused_command_slot_does_not_resurrect_stale_recorded_command_copies()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
            new CommandContextDesc(QueueType.Copy, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        var commands = new RecordedCommands[1];

        backend.Begin(context, default);
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

        backend.Begin(context, default);
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

    private static unsafe void AssertQueueLockPointerUnavailable(
        D3D12CommandQueueLock value)
    {
        try
        {
            _ = (nint)value.Pointer;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new Xunit.Sdk.XunitException(
            "An invalid D3D12 command-queue lock exposed its native pointer.");
    }

    private static void WaitUntilQueueLockInvalidated(D3D12CommandQueueLock value)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        var spinner = new SpinWait();
        while (value.IsHeld)
        {
            Assert.True(limit.Elapsed < TimeSpan.FromSeconds(5), "Queue lock invalidation exceeded the hang limit.");
            spinner.SpinOnce();
        }
    }


}
