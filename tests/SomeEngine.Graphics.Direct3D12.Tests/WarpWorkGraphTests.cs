using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpWorkGraphTests
{
    [Fact]
    public void Cpu_and_gpu_inputs_initialize_preserve_and_reinitialize_on_WARP()
    {
        const string source = """
            struct WorkRecord
            {
                uint Remaining;
            };

            [shader("node")]
            [NodeLaunch("broadcasting")]
            [NodeIsProgramEntry]
            [NodeMaxRecursionDepth(2)]
            [NodeDispatchGrid(1, 1, 1)]
            [numthreads(1, 1, 1)]
            void graphMain(
                DispatchNodeInputRecord<WorkRecord> input,
                [MaxRecords(1)] NodeOutput<WorkRecord> graphMain)
            {
                uint remaining = input.Get().Remaining;
                ThreadNodeOutputRecords<WorkRecord> output =
                    graphMain.GetThreadNodeOutputRecords(remaining > 0 ? 1 : 0);
                if (remaining > 0)
                    output.Get().Remaining = remaining - 1;
                output.OutputComplete();
            }
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("graphMain", SlangStage.Dispatch),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileHlslPassThrough(
            "rhi_work_graph",
            source,
            entries);
        D3D12ValidationOptions validation = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true);
        using IGraphicsBackend backend = new ValidationLayer<D3D12Backend>(
            new D3D12Backend(new D3D12BackendOptions(validation)));
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out WorkGraphs? capability));
        Assert.NotNull(capability);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        Assert.True(capability.CpuInput);
        Assert.True(capability.GpuInput);

        WorkGraphEntryPointLayout[] layouts =
        [
            new(shader.GetEntryPoint(0), 0, 1),
        ];
        using Pipeline pipeline = backend.CreateWorkGraphPipeline(
            device,
            new WorkGraphPipelineDesc(
                shader.Program,
                "RhiWorkGraph",
                layouts,
                [],
                1));
        using Pipeline replacementPipeline = backend.CreateWorkGraphPipeline(
            device,
            new WorkGraphPipelineDesc(
                shader.Program,
                "RhiReplacementWorkGraph",
                layouts,
                [],
                1));
        WorkGraphMemoryRequirements requirements =
            backend.GetWorkGraphMemoryRequirements(pipeline);
        Assert.True(requirements.MaximumSize >= requirements.MinimumSize);
        Assert.True(requirements.MinimumSize > 0);

        using Buffer backing = backend.CreateBuffer(
            device,
            new BufferDesc(requirements.MinimumSize, BufferUsages.ShaderWrite),
            MemoryType.DeviceLocal);
        using Buffer gpuRecords = backend.CreateBuffer(
            device,
            new BufferDesc(sizeof(uint), BufferUsages.ShaderRead),
            MemoryType.Upload);
        BufferRegion backingRegion = new(backing, BufferRange.Whole);
        BufferRegion gpuRecordRegion = new(gpuRecords, BufferRange.Whole);
        byte[] cpuRecord = BitConverter.GetBytes(1U);
        BufferRange recordRange = new(0, sizeof(uint));
        using (MappedBuffer mapping = backend.Map(
            gpuRecords,
            MapType.Write,
            recordRange))
        {
            cpuRecord.CopyTo(mapping.Bytes);
            mapping.Flush(recordRange);
        }

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(
            backing,
            PipelineSync.None,
            PipelineSync.ComputeShading,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetWorkGraphProgram(
            context,
            pipeline,
            backingRegion,
            WorkGraphInitialization.Initialize,
            1);
        backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(0, cpuRecord, 1, sizeof(uint)));
        backend.SetWorkGraphProgram(
            context,
            pipeline,
            backingRegion,
            WorkGraphInitialization.Preserve,
            1);
        backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(0, gpuRecordRegion, 1, sizeof(uint)));
        backend.Barrier(context, new BufferBarrier(
            backing,
            PipelineSync.ComputeShading,
            PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetWorkGraphProgram(
            context,
            pipeline,
            backingRegion,
            WorkGraphInitialization.Initialize,
            1);
        backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(0, cpuRecord, 1, sizeof(uint)));
        using RecordedCommands commands = backend.End(context);
        D3D12CommandStatistics commandStatistics =
            diagnostics!.GetCommandStatistics(commands);
        Assert.Equal(2, commandStatistics.StateSetters.WorkGraphPrograms);

        Queue queue = backend.GetQueue(device, QueueType.Compute);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));

        using CommandContext reuseContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(reuseContext);
        backend.SetWorkGraphProgram(
            reuseContext,
            pipeline,
            backingRegion,
            WorkGraphInitialization.Preserve,
            1);
        backend.DispatchWorkGraph(
            reuseContext,
            new WorkGraphDispatchDesc(0, cpuRecord, 1, sizeof(uint)));
        using RecordedCommands reuseCommands = backend.End(reuseContext);
        QueueCompletion reused = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [reuseCommands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(reused, TimeSpan.FromSeconds(10)));

        using CommandContext replacementContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(replacementContext);
        backend.Barrier(replacementContext, new BufferBarrier(
            backing,
            PipelineSync.ComputeShading,
            PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetWorkGraphProgram(
            replacementContext,
            replacementPipeline,
            backingRegion,
            WorkGraphInitialization.Initialize,
            1);
        backend.DispatchWorkGraph(
            replacementContext,
            new WorkGraphDispatchDesc(0, cpuRecord, 1, sizeof(uint)));
        using RecordedCommands replacementCommands = backend.End(replacementContext);
        QueueCompletion replaced = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [replacementCommands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(replaced, TimeSpan.FromSeconds(10)));
    }
}
