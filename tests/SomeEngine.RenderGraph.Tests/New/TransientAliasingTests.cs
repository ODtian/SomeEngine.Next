using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class TransientAliasingTests
{
    private const ulong ResourceSize = 256;

    [Fact]
    public void Sequential_comparable_resources_share_placement_and_insert_frontier_alias_acquire()
    {
        using Device device = new();
        using Fixture fixture = CreateSequential(device);

        CompiledGraph compiled = Compiler.Compile(
            fixture.Graph,
            device.Compilation,
            optimized: true,
            enableTransientAliasing: true,
            enableRenderPassMerging: false);

        CompiledPlacement first = compiled.Placements[fixture.FirstResource];
        CompiledPlacement second = compiled.Placements[fixture.SecondResource];
        Assert.True(first.IsPlaced);
        Assert.Equal(first.Heap, second.Heap);
        Assert.Equal(first.Offset, second.Offset);

        int aliasUnitOrdinal = Assert.Single(
            Enumerable.Range(0, compiled.RecordUnits.Length),
            index => compiled.RecordUnits[index].Kind == CompiledRecordUnitKind.AliasAcquire);
        CompiledRecordUnit aliasUnit = compiled.RecordUnits[aliasUnitOrdinal];
        Assert.Equal(QueueType.Graphics, aliasUnit.Queue);
        Assert.Empty(aliasUnit.LogicalPassOrdinals);
        CompiledAliasAcquire acquire = Assert.Single(aliasUnit.AliasAcquires);
        Assert.Equal(fixture.FirstResource, acquire.BeforeResource);
        Assert.Equal(fixture.SecondResource, acquire.AfterResource);

        int aliasBatch = FindBatch(compiled, aliasUnitOrdinal);
        int endBatch = FindBatch(compiled, compiled.PassToRecordUnit[fixture.FirstEndPass]);
        int startBatch = FindBatch(compiled, compiled.PassToRecordUnit[fixture.SecondStartPass]);
        Assert.Equal(QueueType.Graphics, compiled.ExecutionBatches[aliasBatch].Queue);
        Assert.Contains(aliasUnitOrdinal, compiled.ExecutionBatches[aliasBatch].RecordUnits);
        Assert.Contains(endBatch, compiled.ExecutionBatches[aliasBatch].Dependencies);
        Assert.Contains(aliasBatch, compiled.ExecutionBatches[startBatch].Dependencies);

        Assert.Equal(new CompiledAliasingStatistics(
            Enabled: true,
            LogicalRequestedBytes: 512,
            NonAliasedPlacedBytes: 512,
            PlannedHeapBytes: 256,
            AliasSavingsBytes: 256,
            AliasSlotCount: 1,
            AliasAcquireCount: 1), compiled.Aliasing);
    }

    [Fact]
    public void Incomparable_async_branches_do_not_share_placement()
    {
        using Device device = new();
        using Fixture fixture = CreateIncomparableBranches(device);

        CompiledGraph compiled = Compiler.Compile(
            fixture.Graph,
            device.Compilation,
            optimized: true,
            enableTransientAliasing: true,
            enableRenderPassMerging: false);

        CompiledPlacement first = compiled.Placements[fixture.FirstResource];
        CompiledPlacement second = compiled.Placements[fixture.SecondResource];
        Assert.Equal(first.Heap, second.Heap);
        Assert.NotEqual(first.Offset, second.Offset);
        Assert.DoesNotContain(compiled.RecordUnits, unit => unit.Kind == CompiledRecordUnitKind.AliasAcquire);
        Assert.Equal(new CompiledAliasingStatistics(
            Enabled: true,
            LogicalRequestedBytes: 512,
            NonAliasedPlacedBytes: 512,
            PlannedHeapBytes: 512,
            AliasSavingsBytes: 0,
            AliasSlotCount: 2,
            AliasAcquireCount: 0), compiled.Aliasing);
    }

    [Fact]
    public void Transitive_cross_queue_happens_before_allows_aliasing()
    {
        using Device device = new();
        using Fixture fixture = CreateTransitiveCrossQueue(device);

        CompiledGraph compiled = Compiler.Compile(
            fixture.Graph,
            device.Compilation,
            optimized: true,
            enableTransientAliasing: true,
            enableRenderPassMerging: false);

        CompiledPlacement first = compiled.Placements[fixture.FirstResource];
        CompiledPlacement second = compiled.Placements[fixture.SecondResource];
        Assert.Equal(first.Heap, second.Heap);
        Assert.Equal(first.Offset, second.Offset);
        CompiledRecordUnit aliasUnit = Assert.Single(
            compiled.RecordUnits,
            unit => unit.Kind == CompiledRecordUnitKind.AliasAcquire);
        CompiledAliasAcquire acquire = Assert.Single(aliasUnit.AliasAcquires);
        Assert.Equal(fixture.FirstResource, acquire.BeforeResource);
        Assert.Equal(fixture.SecondResource, acquire.AfterResource);

        int aliasUnitOrdinal = Array.FindIndex(
            compiled.RecordUnits,
            unit => unit.Kind == CompiledRecordUnitKind.AliasAcquire);
        int aliasBatch = FindBatch(compiled, aliasUnitOrdinal);
        int endBatch = FindBatch(compiled, compiled.PassToRecordUnit[fixture.FirstEndPass]);
        int startBatch = FindBatch(compiled, compiled.PassToRecordUnit[fixture.SecondStartPass]);
        Assert.Contains(endBatch, compiled.ExecutionBatches[aliasBatch].Dependencies);
        Assert.Contains(aliasBatch, compiled.ExecutionBatches[startBatch].Dependencies);
        Assert.Equal(256UL, compiled.Aliasing.AliasSavingsBytes);
    }

    [Fact]
    public void Incompatible_allocation_profiles_do_not_share_placement()
    {
        using Device device = new();
        using Fixture fixture = CreateSequential(
            device,
            secondUsage: BufferUsage.CopySource | BufferUsage.CopyDestination | BufferUsage.ShaderWrite);

        CompiledGraph compiled = Compiler.Compile(
            fixture.Graph,
            device.Compilation,
            optimized: true,
            enableTransientAliasing: true,
            enableRenderPassMerging: false);

        CompiledPlacement first = compiled.Placements[fixture.FirstResource];
        CompiledPlacement second = compiled.Placements[fixture.SecondResource];
        Assert.NotEqual(first.Heap, second.Heap);
        Assert.DoesNotContain(compiled.RecordUnits, unit => unit.Kind == CompiledRecordUnitKind.AliasAcquire);
        Assert.Equal(0UL, compiled.Aliasing.AliasSavingsBytes);
        Assert.Equal(2, compiled.Aliasing.AliasSlotCount);
        Assert.Equal(0, compiled.Aliasing.AliasAcquireCount);
    }

    [Fact]
    public void Default_compiler_and_render_graph_options_keep_transient_aliasing_disabled()
    {
        using Device device = new();
        using Fixture fixture = CreateSequential(device);

        CompiledGraph publicOverload = Compiler.Compile(
            fixture.Graph,
            device.Compilation,
            optimized: true);
        CompiledGraph conservative = Compiler.Compile(
            fixture.Graph,
            device.Compilation,
            optimized: false,
            enableTransientAliasing: true,
            enableRenderPassMerging: false);

        Assert.False(new RenderGraphOptions().EnableTransientAliasing);
        AssertNoAliasing(publicOverload, fixture);
        AssertNoAliasing(conservative, fixture);
        Assert.True(publicOverload.Optimized);
        Assert.False(conservative.Optimized);
    }

    private static void AssertNoAliasing(CompiledGraph compiled, Fixture fixture)
    {
        CompiledPlacement first = compiled.Placements[fixture.FirstResource];
        CompiledPlacement second = compiled.Placements[fixture.SecondResource];
        Assert.Equal(first.Heap, second.Heap);
        Assert.NotEqual(first.Offset, second.Offset);
        Assert.False(compiled.Aliasing.Enabled);
        Assert.Equal(512UL, compiled.Aliasing.LogicalRequestedBytes);
        Assert.Equal(512UL, compiled.Aliasing.NonAliasedPlacedBytes);
        Assert.Equal(512UL, compiled.Aliasing.PlannedHeapBytes);
        Assert.Equal(0UL, compiled.Aliasing.AliasSavingsBytes);
        Assert.Equal(2, compiled.Aliasing.AliasSlotCount);
        Assert.Equal(0, compiled.Aliasing.AliasAcquireCount);
        Assert.DoesNotContain(compiled.RecordUnits, unit => unit.Kind == CompiledRecordUnitKind.AliasAcquire);
    }

    private static Fixture CreateSequential(
        Device device,
        BufferUsage secondUsage = BufferUsage.CopySource | BufferUsage.CopyDestination)
    {
        GraphRecording recording = new();
        BufferUsage firstUsage = BufferUsage.CopySource | BufferUsage.CopyDestination;
        BufferId first = recording.AddBuffer(new BufferDesc(ResourceSize, firstUsage, "sequential-first"), default);
        BufferId second = recording.AddBuffer(new BufferDesc(ResourceSize, secondUsage, "sequential-second"), default);
        BufferHandle outputHandle = device.CreateBuffer(
            new BufferDesc(ResourceSize * 2, BufferUsage.CopyDestination, "sequential-output"));
        BufferId output = ImportOutput(recording, device, outputHandle);

        int firstStart = AddFullWrite(recording, "first-start", first, QueueType.Copy);
        int firstEnd = AddReadAndOutput(
            recording,
            "first-end",
            first,
            output,
            new BufferRange(0, ResourceSize),
            QueueType.Copy);
        int secondStart = AddFullWrite(recording, "second-start", second, QueueType.Copy);
        int secondEnd = AddReadAndOutput(
            recording,
            "second-end",
            second,
            output,
            new BufferRange(ResourceSize, ResourceSize),
            QueueType.Copy);

        FrozenGraph graph = recording.Freeze(device);
        return new Fixture(
            device,
            graph,
            first.Ordinal,
            second.Ordinal,
            firstStart,
            firstEnd,
            secondStart,
            secondEnd,
            [outputHandle]);
    }

    private static Fixture CreateIncomparableBranches(Device device)
    {
        GraphRecording recording = new();
        BufferUsage usage = BufferUsage.CopySource | BufferUsage.CopyDestination;
        BufferId first = recording.AddBuffer(new BufferDesc(ResourceSize, usage, "async-first"), default);
        BufferId second = recording.AddBuffer(new BufferDesc(ResourceSize, usage, "async-second"), default);
        BufferHandle firstOutputHandle = device.CreateBuffer(
            new BufferDesc(ResourceSize, BufferUsage.CopyDestination, "async-first-output"));
        BufferHandle secondOutputHandle = device.CreateBuffer(
            new BufferDesc(ResourceSize, BufferUsage.CopyDestination, "async-second-output"));
        BufferId firstOutput = ImportOutput(recording, device, firstOutputHandle);
        BufferId secondOutput = ImportOutput(recording, device, secondOutputHandle);

        int firstStart = AddFullWrite(recording, "async-first-start", first, QueueType.Copy);
        int secondStart = AddFullWrite(recording, "async-second-start", second, QueueType.Compute);
        int firstEnd = AddReadAndOutput(
            recording,
            "async-first-end",
            first,
            firstOutput,
            BufferRange.Whole,
            QueueType.Copy);
        int secondEnd = AddReadAndOutput(
            recording,
            "async-second-end",
            second,
            secondOutput,
            BufferRange.Whole,
            QueueType.Compute);

        FrozenGraph graph = recording.Freeze(device);
        return new Fixture(
            device,
            graph,
            first.Ordinal,
            second.Ordinal,
            firstStart,
            firstEnd,
            secondStart,
            secondEnd,
            [firstOutputHandle, secondOutputHandle]);
    }

    private static Fixture CreateTransitiveCrossQueue(Device device)
    {
        GraphRecording recording = new();
        BufferUsage transientUsage = BufferUsage.CopySource | BufferUsage.CopyDestination;
        BufferId first = recording.AddBuffer(new BufferDesc(ResourceSize, transientUsage, "transitive-first"), default);
        BufferId second = recording.AddBuffer(new BufferDesc(ResourceSize, transientUsage, "transitive-second"), default);
        BufferHandle firstTokenHandle = device.CreateBuffer(
            new BufferDesc(ResourceSize, transientUsage, "transitive-token-a"));
        BufferHandle secondTokenHandle = device.CreateBuffer(
            new BufferDesc(ResourceSize, transientUsage, "transitive-token-b"));
        BufferHandle outputHandle = device.CreateBuffer(
            new BufferDesc(ResourceSize, BufferUsage.CopyDestination, "transitive-output"));
        BufferId firstToken = ImportProducedBuffer(recording, device, firstTokenHandle);
        BufferId secondToken = ImportProducedBuffer(recording, device, secondTokenHandle);
        BufferId output = ImportOutput(recording, device, outputHandle);

        int firstStart = AddFullWrite(recording, "transitive-first-start", first, QueueType.Copy);
        int firstEnd = recording.AddPass("transitive-first-end", new QueueSelection(QueueType.Copy));
        _ = recording.AddBufferAccess(
            firstEnd,
            first,
            ResourceEffect.Read,
            BufferUse.CopySource,
            BufferRange.Whole,
            PriorContents.Required,
            WriteCoverage.Partial);
        _ = recording.AddBufferAccess(
            firstEnd,
            firstToken,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(firstEnd, Noop);

        int bridge = recording.AddPass("transitive-bridge", QueueSelection.Graphics);
        _ = recording.AddBufferAccess(
            bridge,
            firstToken,
            ResourceEffect.Read,
            BufferUse.CopySource,
            BufferRange.Whole,
            PriorContents.Required,
            WriteCoverage.Partial);
        _ = recording.AddBufferAccess(
            bridge,
            secondToken,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(bridge, Noop);

        int secondStart = recording.AddPass("transitive-second-start", new QueueSelection(QueueType.Compute));
        _ = recording.AddBufferAccess(
            secondStart,
            secondToken,
            ResourceEffect.Read,
            BufferUse.CopySource,
            BufferRange.Whole,
            PriorContents.Required,
            WriteCoverage.Partial);
        _ = recording.AddBufferAccess(
            secondStart,
            second,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(secondStart, Noop);
        int secondEnd = AddReadAndOutput(
            recording,
            "transitive-second-end",
            second,
            output,
            BufferRange.Whole,
            QueueType.Compute);

        FrozenGraph graph = recording.Freeze(device);
        return new Fixture(
            device,
            graph,
            first.Ordinal,
            second.Ordinal,
            firstStart,
            firstEnd,
            secondStart,
            secondEnd,
            [firstTokenHandle, secondTokenHandle, outputHandle]);
    }

    private static int AddFullWrite(
        GraphRecording recording,
        string name,
        BufferId buffer,
        QueueType queue)
    {
        int pass = recording.AddPass(name, new QueueSelection(queue));
        _ = recording.AddBufferAccess(
            pass,
            buffer,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(pass, Noop);
        return pass;
    }

    private static int AddReadAndOutput(
        GraphRecording recording,
        string name,
        BufferId source,
        BufferId output,
        BufferRange outputRange,
        QueueType queue)
    {
        int pass = recording.AddPass(name, new QueueSelection(queue));
        _ = recording.AddBufferAccess(
            pass,
            source,
            ResourceEffect.Read,
            BufferUse.CopySource,
            BufferRange.Whole,
            PriorContents.Required,
            WriteCoverage.Partial);
        _ = recording.AddBufferAccess(
            pass,
            output,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            outputRange,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(pass, Noop);
        return pass;
    }

    private static BufferId ImportOutput(
        GraphRecording recording,
        Device device,
        BufferHandle handle) =>
        recording.AddBuffer(
            device.GetBufferMetadata(handle).Description,
            new ImportedBuffer(
                handle,
                device.GetBufferMetadata(handle),
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                ContentsAvailable: false));

    private static BufferId ImportProducedBuffer(
        GraphRecording recording,
        Device device,
        BufferHandle handle) =>
        recording.AddBuffer(
            device.GetBufferMetadata(handle).Description,
            new ImportedBuffer(
                handle,
                device.GetBufferMetadata(handle),
                BufferUse.CopyDestination,
                BufferUse.CopySource,
                ContentsAvailable: false));

    private static int FindBatch(CompiledGraph graph, int recordUnit)
    {
        int batch = Array.FindIndex(
            graph.ExecutionBatches,
            candidate => candidate.RecordUnits.Contains(recordUnit));
        Assert.True(batch >= 0, $"Record unit {recordUnit} is not owned by an execution batch.");
        return batch;
    }

    private static void Noop(ICommandContext unusedCommands, in PassResources unusedResources) { }

    private sealed class Fixture : IDisposable
    {
        private readonly Device _device;
        private readonly BufferHandle[] _imports;

        public Fixture(
            Device device,
            FrozenGraph graph,
            int firstResource,
            int secondResource,
            int firstStartPass,
            int firstEndPass,
            int secondStartPass,
            int secondEndPass,
            BufferHandle[] imports)
        {
            _device = device;
            Graph = graph;
            FirstResource = firstResource;
            SecondResource = secondResource;
            FirstStartPass = firstStartPass;
            FirstEndPass = firstEndPass;
            SecondStartPass = secondStartPass;
            SecondEndPass = secondEndPass;
            _imports = imports;
        }

        public FrozenGraph Graph { get; }
        public int FirstResource { get; }
        public int SecondResource { get; }
        public int FirstStartPass { get; }
        public int FirstEndPass { get; }
        public int SecondStartPass { get; }
        public int SecondEndPass { get; }

        public void Dispose()
        {
            foreach (BufferHandle import in _imports) _device.DestroyBuffer(import);
            _device.CollectGarbage();
        }
    }
}
