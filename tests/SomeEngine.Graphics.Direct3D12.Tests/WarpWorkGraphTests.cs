using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpWorkGraphTests
{
    private static readonly TimeSpan GpuTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Pinned_slang_work_graph_file_identity_is_fail_closed()
    {
        string probePath = Path.Combine(
            Path.GetTempPath(),
            $"someengine-slang-identity-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(probePath, [1, 2, 3, 4]);
            InvalidDataException mismatch = Assert.Throws<InvalidDataException>(() =>
                D3D12TestShaderProgram.RequirePinnedSlang2026_14File(
                    probePath,
                    new string('0', 64)));
            Assert.Contains("Pinned Slang 2026.14", mismatch.Message, StringComparison.Ordinal);
            Assert.Contains(probePath, mismatch.Message, StringComparison.Ordinal);
            Assert.Contains("Expected SHA-256", mismatch.Message, StringComparison.Ordinal);
            Assert.Contains("actual SHA-256", mismatch.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    [Fact]
    public void Slang_test_target_flags_are_explicit_per_target_family()
    {
        Assert.Equal(
            (SlangTargetFlags)0,
            D3D12TestShaderProgram.TargetFlagsFor(SlangCompileTarget.Dxil));
        Assert.Equal(
            SlangTargetFlags.GenerateSpirvDirectly,
            D3D12TestShaderProgram.TargetFlagsFor(SlangCompileTarget.Spirv));
        Assert.Equal(
            (SlangTargetFlags)0,
            D3D12TestShaderProgram.TargetFlagsFor(SlangCompileTarget.Hlsl));
    }

    [Fact]
    public void Portable_work_graph_limits_are_checked_without_inventing_input_count_state()
    {
        const uint maximumDimension = 65_535;
        const uint maximumVolume = 0x00FF_FFFF;

        Assert.True(WorkGraphValidation.IsMaximumDispatchGridValid(
            maximumDimension, maximumDimension, maximumVolume, 1, 1, 1));
        Assert.True(WorkGraphValidation.IsMaximumDispatchGridValid(
            maximumDimension, maximumDimension, maximumVolume, maximumDimension, 1, 1));
        Assert.False(WorkGraphValidation.IsMaximumDispatchGridValid(
            maximumDimension, maximumDimension, maximumVolume, 0, 1, 1));
        Assert.False(WorkGraphValidation.IsMaximumDispatchGridValid(
            maximumDimension, maximumDimension, maximumVolume, 65_536, 1, 1));
        Assert.False(WorkGraphValidation.IsMaximumDispatchGridValid(
            maximumDimension, maximumDimension, maximumVolume, 4_096, 4_096, 1));

        Assert.True(WorkGraphValidation.IsEntryPointLayoutValid(64, 0, 0));
        Assert.True(WorkGraphValidation.IsEntryPointLayoutValid(64, 64, 16));
        Assert.False(WorkGraphValidation.IsEntryPointLayoutValid(64, 68, 4));
        Assert.False(WorkGraphValidation.IsEntryPointLayoutValid(64, 12, 8));
        Assert.False(WorkGraphValidation.IsEntryPointLayoutValid(64, 4, 0));
        Assert.False(WorkGraphValidation.IsEntryPointLayoutValid(64, 0, 4));

        WorkGraphMemoryRequirements requirements = new(64, 160, 32);
        Assert.Equal(64UL, requirements.NormalizeBackingSize(64));
        Assert.Equal(64UL, requirements.NormalizeBackingSize(80));
        Assert.Equal(96UL, requirements.NormalizeBackingSize(100));
        Assert.Equal(160UL, requirements.NormalizeBackingSize(1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => requirements.NormalizeBackingSize(63));

        Assert.DoesNotContain(
            typeof(WorkGraphs).GetProperties(),
            static property => property.Name.Contains(
                "MaximumInputRecordCount",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(WorkGraphPipelineDesc).GetProperties(),
            static property => property.Name.Contains(
                "MaximumInputRecordCount",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Slang_reflection_exposes_work_graph_attributes_without_an_extra_model()
    {
        const string source = """
            import experimental.workgraph;

            struct WorkRecord { uint Value; };

            [shader("node")]
            [NodeID("renamedNode", 3)]
            [NodeLaunch("broadcasting")]
            [NodeIsProgramEntry]
            [NodeMaxDispatchGrid(8, 2, 1)]
            [numthreads(1, 1, 1)]
            void graphMain(DispatchNodeInputRecord<WorkRecord> input)
            {
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileExperimental(
            "slang_work_graph_attribute_facts",
            source,
            [new("graphMain", SlangStage.Node)]);
        EntryPointReflection entry = shader.GetEntryPoint(0);
        FunctionReflection function = entry.Function;
        Assert.NotEqual(FunctionReflection.Null, function);
        IGlobalSession globalSession = shader.Program.GetSession().GetGlobalSession();
        AttributeReflection nodeID = function.FindAttributeByName(globalSession, "NodeID")
            ?? AttributeReflection.Null;
        Assert.NotEqual(AttributeReflection.Null, nodeID);
        Assert.Equal("renamedNode", nodeID.GetArgumentValueString(0));
        Assert.Equal(2u, nodeID.ArgumentCount);
        Assert.NotEqual(
            AttributeReflection.Null,
            function.FindAttributeByName(globalSession, "NodeIsProgramEntry")
                ?? AttributeReflection.Null);
    }

    [Fact]
    public void Pipeline_materializes_only_exact_slang_entry_identities()
    {
        const string source = """
            import experimental.workgraph;

            [shader("node")]
            [NodeID("entryA", 2)]
            [NodeLaunch("broadcasting")]
            [NodeIsProgramEntry]
            [NodeDispatchGrid(1, 1, 1)]
            [numthreads(1, 1, 1)]
            void graphA() { }

            [shader("node")]
            [NodeLaunch("broadcasting")]
            [NodeIsProgramEntry]
            [NodeDispatchGrid(1, 1, 1)]
            [numthreads(1, 1, 1)]
            void graphB() { }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileExperimental(
            "work_graph_exact_entry_identity",
            source,
            [
                new("graphA", SlangStage.Node),
                new("graphB", SlangStage.Node),
            ]);
        EntryPointReflection entryA = shader.GetEntryPoint(0);
        EntryPointReflection entryB = shader.GetEntryPoint(1);
        using D3D12Backend backend = CreateBackend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateWorkGraphPipeline(
            device,
            new WorkGraphPipelineDesc(shader.Program));

        Assert.False(backend.TryGetWorkGraphEntryPoints(pipeline, [], out int count));
        Assert.Equal(2, count);
        var entries = new WorkGraphEntryPointInfo[count];
        Assert.True(backend.TryGetWorkGraphEntryPoints(pipeline, entries, out int confirmed));
        Assert.Equal(count, confirmed);

        WorkGraphEntryPointInfo materializedA = Assert.Single(entries, entry => entry.EntryPoint == entryA);
        WorkGraphEntryPointInfo materializedB = Assert.Single(entries, entry => entry.EntryPoint == entryB);
        Assert.Equal(0U, materializedA.RecordSize);
        Assert.Equal(0U, materializedA.RecordAlignment);
        Assert.Equal(0U, materializedB.RecordSize);
        Assert.Equal(0U, materializedB.RecordAlignment);
    }

    [Fact]
    public void Single_and_multi_node_cpu_and_gpu_inputs_execute_on_WARP()
    {
        const string source = """
            import experimental.workgraph;

            struct WorkRecord { uint Value; };
            RWStructuredBuffer<uint> outputValues;

            [shader("node")]
            [NodeID("nodeA")]
            [NodeLaunch("broadcasting")]
            [NodeIsProgramEntry]
            [NodeDispatchGrid(1, 1, 1)]
            [numthreads(1, 1, 1)]
            void nodeA(DispatchNodeInputRecord<WorkRecord> input)
            {
                outputValues[0] = input.Get().Value;
            }

            [shader("node")]
            [NodeID("nodeB")]
            [NodeLaunch("broadcasting")]
            [NodeIsProgramEntry]
            [NodeDispatchGrid(1, 1, 1)]
            [numthreads(1, 1, 1)]
            void nodeB(DispatchNodeInputRecord<WorkRecord> input)
            {
                outputValues[1] = input.Get().Value;
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileExperimental(
            "work_graph_four_input_modes",
            source,
            [
                new("nodeA", SlangStage.Node),
                new("nodeB", SlangStage.Node),
            ]);
        EntryPointReflection entryA = shader.GetEntryPoint(0);
        EntryPointReflection entryB = shader.GetEntryPoint(1);
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;

        using D3D12Backend backend = CreateBackend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out WorkGraphs? capability));
        Assert.NotNull(capability);
        Assert.True(capability.CpuInput);
        Assert.True(capability.GpuInput);
        using Pipeline pipeline = backend.CreateWorkGraphPipeline(
            device,
            new WorkGraphPipelineDesc(shader.Program));
        WorkGraphEntryPointInfo[] entryInfos = GetEntryPoints(backend, pipeline);
        WorkGraphEntryPointInfo infoA = Assert.Single(entryInfos, value => value.EntryPoint == entryA);
        WorkGraphEntryPointInfo infoB = Assert.Single(entryInfos, value => value.EntryPoint == entryB);
        Assert.Equal((uint)sizeof(uint), infoA.RecordSize);
        Assert.Equal((uint)sizeof(uint), infoB.RecordSize);

        WorkGraphMemoryRequirements requirements =
            backend.GetWorkGraphMemoryRequirements(pipeline);
        using Buffer? backing = CreateBacking(backend, device, requirements);
        BufferRegion? backingRegion = backing is null
            ? null
            : new BufferRegion(backing, BufferRange.Whole);
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(2 * sizeof(uint), BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer gpuA = CreateUploadRecord(backend, device, 55);
        using Buffer gpuB = CreateUploadRecord(backend, device, 66);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(2 * sizeof(uint), BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));

        backend.Begin(context, new CommandRecordingDesc(
            InitialCapturedResourceCapacity: 16,
            InitialResourceDescriptorCapacity: 32,
            InitialSamplerDescriptorCapacity: 8));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.None,
            PipelineSync.ComputeShading,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        if (backing is not null)
        {
            backend.Barrier(context, new BufferBarrier(
                backing,
                PipelineSync.None,
                PipelineSync.ComputeShading,
                ResourceAccess.NoAccess,
                ResourceAccess.UnorderedAccess));
        }
        backend.BindWorkGraph(
            context,
            pipeline,
            backingRegion,
            WorkGraphInitialization.Initialize);
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(
                globals,
                [ResourceBinding.WritableBuffer(outputUav)],
                []));

        backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(entryA, BitConverter.GetBytes(11u), 1, infoA.RecordSize));
        InsertUavOrdering(backend, context, output);

        backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(
                entryB,
                new BufferRegion(gpuB, BufferRange.Whole),
                1,
                infoB.RecordSize));
        InsertUavOrdering(backend, context, output);

        byte[] multiCpuRecords = MemoryMarshal.AsBytes<uint>([33u, 44u]).ToArray();
        WorkGraphCpuNodeInput[] cpuInputs =
        [
            new(entryA, 0, 1, infoA.RecordSize),
            new(entryB, sizeof(uint), 1, infoB.RecordSize),
        ];
        backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(cpuInputs, multiCpuRecords));
        InsertUavOrdering(backend, context, output);

        WorkGraphGpuNodeInput[] gpuInputs =
        [
            new(entryA, new BufferRegion(gpuA, BufferRange.Whole), 1, infoA.RecordSize),
            new(entryB, new BufferRegion(gpuB, BufferRange.Whole), 1, infoB.RecordSize),
        ];
        backend.DispatchWorkGraph(context, new WorkGraphDispatchDesc(gpuInputs));

        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.ComputeShading,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(
            context,
            new BufferCopy(output, 0, readback, 0, 2 * sizeof(uint)));
        using RecordedCommands commands = backend.End(context);
        SubmitAndWait(backend, device, commands);

        Assert.Equal([55u, 66u], ReadUInt32(backend, readback, 2));
    }

    [Fact]
    public void Validation_rejects_foreign_entry_and_malformed_multi_input_before_forwarding()
    {
        const string source = """
            import experimental.workgraph;

            [shader("node")]
            [NodeLaunch("broadcasting")]
            [NodeIsProgramEntry]
            [NodeDispatchGrid(1, 1, 1)]
            [numthreads(1, 1, 1)]
            void graphMain() { }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileExperimental(
            "work_graph_validation_identity",
            source,
            [new("graphMain", SlangStage.Node)]);
        using D3D12TestShaderProgram foreign = D3D12TestShaderProgram.CompileExperimental(
            "work_graph_validation_identity_foreign",
            source,
            [new("graphMain", SlangStage.Node)]);
        EntryPointReflection entry = shader.GetEntryPoint(0);
        EntryPointReflection foreignEntry = foreign.GetEntryPoint(0);

        using IGraphicsBackend backend = CreateValidatedBackend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateWorkGraphPipeline(
            device,
            new WorkGraphPipelineDesc(shader.Program));
        WorkGraphMemoryRequirements requirements =
            backend.GetWorkGraphMemoryRequirements(pipeline);
        using Buffer? backing = CreateBacking(backend, device, requirements);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context);
        if (backing is not null)
        {
            backend.Barrier(context, new BufferBarrier(
                backing,
                PipelineSync.None,
                PipelineSync.ComputeShading,
                ResourceAccess.NoAccess,
                ResourceAccess.UnorderedAccess));
        }
        backend.BindWorkGraph(
            context,
            pipeline,
            backing is null ? null : new BufferRegion(backing, BufferRange.Whole),
            WorkGraphInitialization.Initialize);

        Assert.Throws<InvalidOperationException>(() => backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(foreignEntry, [], 1, 0)));
        Assert.Throws<InvalidOperationException>(() => backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(
                [
                    new WorkGraphCpuNodeInput(entry, 0, 1, 0),
                    new WorkGraphCpuNodeInput(entry, 0, 1, 0),
                ],
                [])));

        backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(entry, [], 1, 0));
        using RecordedCommands commands = backend.End(context);
        SubmitAndWait(backend, device, commands);
    }

    [Fact]
    public void Classic_pipeline_invalidates_the_selected_work_graph_program()
    {
        const string graphSource = """
            import experimental.workgraph;

            [shader("node")]
            [NodeLaunch("broadcasting")]
            [NodeIsProgramEntry]
            [NodeDispatchGrid(1, 1, 1)]
            [numthreads(1, 1, 1)]
            void graphMain() { }
            """;
        using D3D12TestShaderProgram graphShader = D3D12TestShaderProgram.CompileExperimental(
            "work_graph_state_reset",
            graphSource,
            [new("graphMain", SlangStage.Node)]);
        using D3D12TestShaderProgram computeShader = D3D12TestShaderProgram.Compile(
            "work_graph_state_reset_compute",
            "[shader(\"compute\")] [numthreads(1, 1, 1)] void computeMain() {}",
            [new("computeMain", SlangStage.Compute)]);
        EntryPointReflection graphEntry = graphShader.GetEntryPoint(0);

        using D3D12Backend backend = CreateBackend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline graph = backend.CreateWorkGraphPipeline(
            device,
            new WorkGraphPipelineDesc(graphShader.Program));
        using Pipeline compute = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(computeShader.Program, computeShader.GetEntryPoint(0)));
        WorkGraphMemoryRequirements requirements = backend.GetWorkGraphMemoryRequirements(graph);
        using Buffer? backing = CreateBacking(backend, device, requirements);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context);
        if (backing is not null)
        {
            backend.Barrier(context, new BufferBarrier(
                backing,
                PipelineSync.None,
                PipelineSync.ComputeShading,
                ResourceAccess.NoAccess,
                ResourceAccess.UnorderedAccess));
        }
        Assert.Throws<InvalidOperationException>(() => backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(graphEntry, [], 1, 0)));
        backend.BindWorkGraph(
            context,
            graph,
            backing is null ? null : new BufferRegion(backing, BufferRange.Whole),
            WorkGraphInitialization.Initialize);
        backend.DispatchWorkGraph(context, new WorkGraphDispatchDesc(graphEntry, [], 1, 0));
        backend.SetPipeline(context, compute);
        Assert.Throws<InvalidOperationException>(() => backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(graphEntry, [], 1, 0)));
        using RecordedCommands commands = backend.End(context);
        SubmitAndWait(backend, device, commands);
    }

    private static D3D12Backend CreateBackend() => new(
        new D3D12BackendOptions(new D3D12ValidationOptions(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true)));

    private static ValidationLayer CreateValidatedBackend() => new(
        CreateBackend());

    private static WorkGraphEntryPointInfo[] GetEntryPoints(
        IGraphicsBackend backend,
        Pipeline pipeline)
    {
        Assert.False(backend.TryGetWorkGraphEntryPoints(pipeline, [], out int count));
        var entries = new WorkGraphEntryPointInfo[count];
        Assert.True(backend.TryGetWorkGraphEntryPoints(pipeline, entries, out int confirmed));
        Assert.Equal(count, confirmed);
        return entries;
    }

    private static Buffer? CreateBacking(
        IGraphicsBackend backend,
        Device device,
        in WorkGraphMemoryRequirements requirements)
    {
        if (requirements.MaximumSize == 0)
            return null;
        ulong size = requirements.MinimumSize == 0
            ? requirements.MaximumSize
            : requirements.MinimumSize;
        return backend.CreateBuffer(
            device,
            new BufferDesc(size, BufferUsages.ShaderWrite),
            MemoryType.DeviceLocal);
    }

    private static Buffer CreateUploadRecord(
        IGraphicsBackend backend,
        Device device,
        uint value)
    {
        Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(sizeof(uint), BufferUsages.ShaderRead),
            MemoryType.Upload);
        try
        {
            BufferRange range = new(0, sizeof(uint));
            using MappedBuffer mapped = backend.Map(buffer, MapType.Write, range);
            Assert.True(BitConverter.TryWriteBytes(mapped.Bytes, value));
            mapped.Flush(range);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static void InsertUavOrdering(
        IGraphicsBackend backend,
        CommandContext context,
        Buffer buffer) =>
        backend.Barrier(context, new BufferBarrier(
            buffer,
            PipelineSync.ComputeShading,
            PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.UnorderedAccess));

    private static void SubmitAndWait(
        IGraphicsBackend backend,
        Device device,
        RecordedCommands commands)
    {
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, GpuTimeout));
        backend.CollectCompleted(device);
    }

    private static uint[] ReadUInt32(
        IGraphicsBackend backend,
        Buffer buffer,
        int count)
    {
        BufferRange range = new(0, checked((ulong)count * sizeof(uint)));
        using MappedBuffer mapped = backend.Map(buffer, MapType.Read, range);
        mapped.Invalidate(range);
        return MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray();
    }
}
