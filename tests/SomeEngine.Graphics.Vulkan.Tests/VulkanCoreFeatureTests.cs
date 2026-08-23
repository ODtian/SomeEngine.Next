namespace SomeEngine.Graphics.Vulkan.Tests;

using SlangShaderSharp;
using Xunit;

public sealed class VulkanCoreFeatureTests
{
    [Fact]
    public void Public_factory_dispatches_through_graphics_backend_contract()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        AdapterInfo[] adapters = new AdapterInfo[8];
        Assert.True(backend.TryEnumerateAdapters(default, adapters, out int count));
        Assert.True(count > 0);
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        Assert.Equal(256UL, buffer.Info.Size);
    }

    [Fact]
    public void Timestamp_query_resolves_to_buffer()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using QueryPool queries = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(8, BufferUsages.CopyDestination | BufferUsages.QueryResolve),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.WriteTimestamp(context, queries, 0);
        backend.ResolveQueries(context, queries, 0, 1, readback, BufferRange.Whole);
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.NotEqual(0UL, BitConverter.ToUInt64(mapped.Bytes));
    }

    [Fact]
    public void Pipeline_cache_round_trips_and_merges_native_data()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID) {}
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using PipelineCache first = backend.CreatePipelineCache(device, default);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[0]),
            first);
        Assert.False(backend.TryGetPipelineCacheData(first, [], out int required));
        byte[] data = new byte[required];
        Assert.True(backend.TryGetPipelineCacheData(first, data, out int written));
        Assert.Equal(required, written);
        using PipelineCache second = backend.CreatePipelineCache(device, new PipelineCacheDesc(data));
        backend.MergePipelineCaches(first, [second]);
    }

    [Fact]
    public void Core_indirect_dispatch_records_and_submits()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID) {}
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            optionalFeatures: DeviceFeatures.IndirectCommands));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[0]));
        using Buffer arguments = backend.CreateBuffer(
            device,
            new BufferDesc(12, BufferUsages.CopySource | BufferUsages.Indirect),
            MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(arguments, MapType.Write, BufferRange.Whole))
        {
            BitConverter.TryWriteBytes(mapped.Bytes[0..4], 1u);
            BitConverter.TryWriteBytes(mapped.Bytes[4..8], 1u);
            BitConverter.TryWriteBytes(mapped.Bytes[8..12], 1u);
            mapped.Flush(mapped.Range);
        }
        using IndirectCommandLayout layout = backend.CreateIndirectCommandLayout(
            device,
            new IndirectCommandLayoutDesc(
                [new IndirectArgumentDesc(IndirectArgumentType.Dispatch)],
                12));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.ExecuteIndirect(
            context,
            layout,
            new BufferRegion(arguments, BufferRange.Whole),
            1);
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Memory_budget_residency_and_calibrated_timestamps_are_live()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.Residency | DeviceFeatures.CalibratedTimestamps));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        ResidencyInfo budget = backend.GetResidencyInfo(device);
        Assert.True(budget.LocalBudget > 0);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(4096, BufferUsages.CopyDestination));
        ResidencyResource resource = backend.GetResidencyResource(buffer);
        QueueCompletion completion = backend.EnqueueMakeResident(queue, [resource]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));
        backend.Evict(device, [resource]);
        CalibratedTimestampInfo timestamps = backend.CalibrateTimestamps(queue);
        Assert.True(timestamps.CpuCounter > 0);
        Assert.True(timestamps.CpuFrequency > 0);
        Assert.True(timestamps.QueueCounter > 0);
        Assert.True(timestamps.QueueFrequency > 0);
    }

    [Fact]
    public void External_timeline_exports_imports_and_orders_queues()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues =
        [
            new DeviceQueueDesc(QueueType.Graphics),
            new DeviceQueueDesc(QueueType.Compute),
        ];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.ExternalTimelines));
        using ExternalTimeline timeline = backend.CreateExternalTimeline(device, 0);
        using ExternalHandle handle = backend.ExportTimeline(
            timeline,
            ExternalHandleType.OpaqueWin32);
        using ExternalTimeline imported = backend.ImportTimeline(device, handle);
        Queue graphics = backend.GetQueue(device, QueueType.Graphics);
        Queue compute = backend.GetQueue(device, QueueType.Compute);
        QueueCompletion signal = backend.Submit(
            graphics,
            new QueueSubmitDesc([], [], [], [], [new TimelineSignal(timeline, 1)]));
        QueueCompletion wait = backend.Submit(
            compute,
            new QueueSubmitDesc([], [new TimelinePoint(imported, 1)], [], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(signal, TimeSpan.FromSeconds(2)));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(wait, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void External_buffer_memory_exports_imports_and_shares_bytes()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.ExternalResources));
        BufferDesc sharedDescription = new(
            256,
            BufferUsages.CopySource | BufferUsages.CopyDestination | BufferUsages.Shareable);
        using Buffer original = backend.CreateBuffer(device, sharedDescription);
        using ExternalHandle handle = backend.ExportBuffer(
            original,
            ExternalHandleType.OpaqueWin32);
        using Buffer imported = backend.ImportBuffer(
            device,
            handle,
            sharedDescription,
            new ImportedResourceState(
                PipelineSync.None,
                ResourceAccess.NoAccess,
                null,
                QueueType.Graphics));
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using (MappedBuffer mapped = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            mapped.Bytes.Fill(0xA7);
            mapped.Flush(mapped.Range);
        }
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, original, 0, 256));
        backend.Barrier(context, new MemoryBarrier(
            PipelineSync.Copy,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(imported, 0, readback, 0, 256));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        Assert.All(result.Bytes.ToArray(), static value => Assert.Equal(0xA7, value));
    }
}
