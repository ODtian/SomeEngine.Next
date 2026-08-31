namespace SomeEngine.Graphics.Vulkan.Tests;

using SlangShaderSharp;
using Xunit;
using Xunit.Sdk;

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
    public void Counted_indirect_dispatch_executes_device_generated_sequences()
    {
        const string source = """
            RWStructuredBuffer<uint> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                InterlockedAdd(outputValues[0], 1);
            }
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            optionalFeatures: DeviceFeatures.IndirectCommands));
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[0]));
        using Buffer arguments = backend.CreateBuffer(
            device,
            new BufferDesc(36, BufferUsages.Indirect),
            MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(arguments, MapType.Write, BufferRange.Whole))
        {
            mapped.Bytes.Clear();
            for (int command = 0; command < 2; command++)
            {
                int offset = command * 12;
                BitConverter.TryWriteBytes(mapped.Bytes[offset..], 1u);
                BitConverter.TryWriteBytes(mapped.Bytes[(offset + 4)..], 1u);
                BitConverter.TryWriteBytes(mapped.Bytes[(offset + 8)..], 1u);
            }
            mapped.Flush(mapped.Range);
        }
        using Buffer count = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.Indirect),
            MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(count, MapType.Write, BufferRange.Whole))
        {
            BitConverter.TryWriteBytes(mapped.Bytes, 2u);
            mapped.Flush(mapped.Range);
        }
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                output,
                BufferRange.Whole,
                StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.CopyDestination),
            MemoryType.Readback);
        IndirectCommandLayout? layout = null;
        try
        {
            layout = backend.CreateIndirectCommandLayout(
                device,
                new IndirectCommandLayoutDesc(
                    [new IndirectArgumentDesc(IndirectArgumentType.Dispatch)],
                    12,
                    pipeline));
        }
        catch (NotSupportedException)
        {
            throw SkipException.ForSkip(
                "The Vulkan adapter does not expose VK_EXT_device_generated_commands.");
        }
        using (layout)
        using (CommandContext context = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Graphics, 0, 1)))
        {
            backend.Begin(context);
            backend.Barrier(context, new BufferBarrier(
                output,
                PipelineSync.None,
                PipelineSync.ComputeShading,
                ResourceAccess.NoAccess,
                ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, pipeline);
            backend.SetTransientParameterBindings(
                context,
                new ParameterBlockBindings(
                    globals,
                    [ResourceBinding.WritableBuffer(outputUav)],
                    []));
            backend.ExecuteIndirect(
                context,
                layout,
                new BufferRegion(arguments, BufferRange.Whole),
                3,
                new BufferRegion(count, BufferRange.Whole));
            backend.Barrier(context, new BufferBarrier(
                output,
                PipelineSync.ComputeShading,
                PipelineSync.Copy,
                ResourceAccess.UnorderedAccess,
                ResourceAccess.CopySource));
            backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
            using RecordedCommands commands = backend.End(context);
            Queue queue = backend.GetQueue(device, QueueType.Graphics);
            QueueCompletion completion = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], [commands], [], []));
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
            using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
            mapped.Invalidate(mapped.Range);
            Assert.Equal(2u, BitConverter.ToUInt32(mapped.Bytes));
        }
    }

    [Fact]
    public void Residency_is_not_published_and_calibrated_timestamps_remain_live()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        Assert.Throws<NotSupportedException>(() => backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.Residency)));
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.CalibratedTimestamps));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
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
    public void External_memory_is_heap_only_and_placed_buffers_share_bytes()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        DeviceDesc deviceDescription = new(
            default,
            queues,
            requiredFeatures: DeviceFeatures.ExternalResources);
        using Device device = backend.CreateDevice(deviceDescription);
        using Device importedDevice = backend.CreateDevice(deviceDescription);
        BufferDesc sharedDescription = new(
            256,
            BufferUsages.CopySource | BufferUsages.CopyDestination | BufferUsages.Shareable);
        Assert.True(backend.TryGetCapability(device, out ExternalResources? external));
        Assert.NotNull(external);
        Assert.False(external.SupportsBufferImport(ExternalHandleType.OpaqueWin32));
        Assert.False(external.SupportsBufferExport(ExternalHandleType.OpaqueWin32));
        Assert.False(external.SupportsTextureImport(ExternalHandleType.OpaqueWin32));
        Assert.False(external.SupportsTextureExport(ExternalHandleType.OpaqueWin32));
        Assert.True(external.SupportsHeapImport(ExternalHandleType.OpaqueWin32));
        Assert.True(external.SupportsHeapExport(ExternalHandleType.OpaqueWin32));

        MemoryRequirements requirements = backend.GetBufferMemoryRequirements(
            device,
            sharedDescription);
        ulong heapSize = Math.Max(requirements.Size, requirements.Alignment);
        HeapDesc heapDescription = new(
            heapSize,
            requirements.Alignment,
            MemoryType.DeviceLocal,
            HeapFlags.Buffers | HeapFlags.Shareable);
        using Heap originalHeap = backend.CreateHeap(device, heapDescription);
        using Buffer original = backend.CreatePlacedBuffer(
            device,
            originalHeap,
            0,
            sharedDescription);
        Assert.Throws<NotSupportedException>(() => backend.ExportBuffer(
            original,
            ExternalHandleType.OpaqueWin32));
        using ExternalHandle handle = backend.ExportHeap(
            originalHeap,
            ExternalHandleType.OpaqueWin32);
        using Heap importedHeap = backend.ImportHeap(importedDevice, handle, heapDescription);
        using Buffer imported = backend.CreatePlacedBuffer(
            importedDevice,
            importedHeap,
            0,
            sharedDescription);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
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
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));

        using Buffer readback = backend.CreateBuffer(
            importedDevice,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);
        Queue importedQueue = backend.GetQueue(importedDevice, QueueType.Graphics);
        using CommandContext importedContext = backend.CreateCommandContext(
            importedDevice,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(importedContext);
        backend.CopyBuffer(importedContext, new BufferCopy(imported, 0, readback, 0, 256));
        using RecordedCommands importedCommands = backend.End(importedContext);
        QueueCompletion importedCompletion = backend.Submit(
            importedQueue,
            new QueueSubmitDesc([], [], [importedCommands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(importedCompletion, TimeSpan.FromSeconds(2)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        Assert.All(result.Bytes.ToArray(), static value => Assert.Equal(0xA7, value));
    }
}
