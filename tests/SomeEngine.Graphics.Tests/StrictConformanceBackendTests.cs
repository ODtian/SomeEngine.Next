using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class StrictConformanceBackendTests
{
    [Fact]
    public void Device_feature_flags_are_creation_requests_and_typed_capabilities_are_authoritative()
    {
        using var backend = new StrictConformanceBackend();
        AdapterInfo adapter = GetAdapter(backend);
        DeviceQueueDesc[] queues = [new(QueueType.Graphics)];

        using Device requiredPresentation = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            queues,
            requiredFeatures: DeviceFeatures.Presentation));

        Assert.True(backend.TryGetCapability(
            requiredPresentation,
            out Presentation? presentation));
        Assert.NotNull(presentation);
        Assert.False(backend.TryGetCapability(requiredPresentation, out MeshShaders? mesh));
        Assert.Null(mesh);

        using Device optionalUnsupported = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            queues,
            optionalFeatures: DeviceFeatures.MeshShaders));
        Assert.False(backend.TryGetCapability(optionalUnsupported, out MeshShaders? optionalMesh));
        Assert.Null(optionalMesh);

        Assert.Throws<NotSupportedException>(() => backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            queues,
            requiredFeatures: DeviceFeatures.MeshShaders)));
    }

    [Fact]
    public void Portable_resource_descriptor_command_query_and_pipeline_contracts_execute_without_D3D12()
    {
        using var backend = new StrictConformanceBackend();
        AdapterInfo adapter = GetAdapter(backend);
        DeviceQueueDesc[] queues =
        [
            new(QueueType.Graphics),
            new(QueueType.Compute),
            new(QueueType.Copy),
        ];
        using Device device = backend.CreateDevice(new DeviceDesc(adapter.Id, queues));
        byte[] expected = Enumerable.Range(0, 256)
            .Select(static value => unchecked((byte)(value * 37 + 11)))
            .ToArray();
        Heap heap = backend.CreateHeap(device, new HeapDesc(
            512,
            256,
            MemoryType.Upload,
            HeapFlags.Buffers));
        Buffer source = backend.CreatePlacedBuffer(
            device,
            heap,
            0,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.ShaderRead));
        using Buffer destination = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using Buffer queryResults = backend.CreateBuffer(
            device,
            new BufferDesc(8, BufferUsages.QueryResolve),
            MemoryType.Readback);
        using (MappedBuffer mapped = backend.Map(source, MapType.Write, BufferRange.Whole))
        {
            expected.CopyTo(mapped.Bytes);
            mapped.Flush(new BufferRange(0, 256));
        }

        BufferSrv view = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(source, BufferRange.Whole, Format.R32UInt));
        DescriptorSlotDesc slot = new(ResourceBindingType.BufferSrv, Format.R32UInt);
        DescriptorTable firstTable = backend.CreateDescriptorTable(device, [slot]);
        DescriptorTable secondTable = backend.CreateDescriptorTable(device, [slot]);
        backend.WriteDescriptor(firstTable, 0, ResourceBinding.ReadOnlyBuffer(view));
        backend.WriteDescriptor(secondTable, 0, ResourceBinding.ReadOnlyBuffer(view));
        DescriptorIndex firstIndex = backend.GetDescriptorIndex(firstTable, 0);
        DescriptorIndex secondIndex = backend.GetDescriptorIndex(secondTable, 0);
        Assert.Equal(firstIndex.Value, secondIndex.Value);
        Assert.NotEqual(firstIndex, secondIndex);
        backend.PublishDescriptors(device);

        using QueryPool query = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Copy, 1));
        CommandContext copyContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 2));
        backend.Begin(copyContext);
        backend.WriteTimestamp(copyContext, query, 0);
        backend.CopyBuffer(copyContext, new BufferCopy(source, 0, destination, 0, 256));
        backend.ResolveQueries(
            copyContext,
            query,
            0,
            1,
            queryResults,
            BufferRange.Whole);
        RecordedCommands copyCommands = backend.End(copyContext);

        copyContext.Dispose();
        view.Dispose();
        firstTable.Dispose();
        secondTable.Dispose();
        source.Dispose();
        heap.Dispose();

        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);
        QueueCompletion copyCompletion = backend.Submit(
            copyQueue,
            new QueueSubmitDesc([], [], [copyCommands], [], []));
        copyCommands.Dispose();
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(copyCompletion, TimeSpan.FromSeconds(1)));
        Assert.True(backend.IsComplete(copyCompletion));

        byte[] actual = new byte[expected.Length];
        using (MappedBuffer mapped = backend.Map(destination, MapType.Read, BufferRange.Whole))
        {
            mapped.Invalidate(new BufferRange(0, 256));
            mapped.Bytes.CopyTo(actual);
        }
        Assert.Equal(expected, actual);
        using (MappedBuffer mapped = backend.Map(queryResults, MapType.Read, BufferRange.Whole))
        {
            mapped.Invalidate(new BufferRange(0, 8));
            Assert.NotEqual(0UL, BitConverter.ToUInt64(mapped.Bytes));
        }

        using ConformanceShaderProgram shader = ConformanceShaderProgram.CompileCompute();
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.EntryPoint));
        using CommandContext computeContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(computeContext);
        backend.SetPipeline(computeContext, pipeline);
        backend.Dispatch(computeContext, new DispatchArguments(1, 1, 1));
        backend.Dispatch(computeContext, new DispatchArguments(1, 1, 1));
        using RecordedCommands computeCommands = backend.End(computeContext);
        QueueCompletion computeCompletion = backend.Submit(
            backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [computeCommands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(computeCompletion, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Presentation_acquire_submit_present_and_reconfigure_sequence_is_backend_portable()
    {
        using var backend = new StrictConformanceBackend();
        AdapterInfo adapter = GetAdapter(backend);
        DeviceQueueDesc[] queues = [new(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            queues,
            requiredFeatures: DeviceFeatures.Presentation));
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            1));
        SwapchainConfig config = new(
            64,
            32,
            Format.R8G8B8A8UNorm,
            ColorSpace.Srgb,
            PresentType.Fifo,
            AllowTearing: false,
            MaximumFrameLatency: 2);
        using Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(
                surface,
                2,
                TextureUsages.ColorAttachment | TextureUsages.CopySource,
                config));

        Assert.Equal(
            SwapchainAcquireStatus.Success,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(1)),
                out SwapchainImage image));
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [], [image], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.Zero));
        Assert.Equal(SwapchainImageStatus.Submitted, image.Status);
        Assert.Equal(PresentStatus.Success, backend.Present(queue, image));
        Assert.Equal(SwapchainImageStatus.Presented, image.Status);

        SwapchainConfig resized = config with { Width = 96, Height = 48 };
        Assert.Equal(ReconfigureStatus.Success, backend.Reconfigure(swapchain, resized));
        Assert.Equal(resized, swapchain.Info.Config);
        Assert.Throws<InvalidOperationException>(() => _ = image.Status);
    }

    [Fact]
    public async Task Async_pipeline_creation_is_part_of_the_portable_backend_surface()
    {
        using var backend = new StrictConformanceBackend();
        AdapterInfo adapter = GetAdapter(backend);
        using Device device = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Compute)]));
        using ConformanceShaderProgram shader = ConformanceShaderProgram.CompileCompute();

        using Pipeline pipeline = await backend.CreateComputePipelineAsync(
            device,
            new ComputePipelineDesc(shader.Program, shader.EntryPoint));

        Assert.Equal(PipelineType.Compute, pipeline.Type);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.Zero));
    }

    private static AdapterInfo GetAdapter(IGraphicsBackend backend)
    {
        AdapterEnumerationOptions options = new(IncludeSoftware: true);
        Assert.False(backend.TryEnumerateAdapters(options, [], out int count));
        Assert.Equal(1, count);
        var adapters = new AdapterInfo[count];
        Assert.True(backend.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(count, confirmed);
        return Assert.Single(adapters);
    }
}
