using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Xunit;
using NullDevice = SomeEngine.Graphics.Null.Device;
using NullOptions = SomeEngine.Graphics.Null.Options;

namespace SomeEngine.RenderGraph.Tests;

public sealed class PassCullingTests
{
    private static readonly PassExecution Noop = static (ICommandContext _, in PassResources _) => { };

    [Fact]
    public void Buffer_liveness_keeps_the_latest_producer_for_each_exact_segment()
    {
        using NullDevice device = new();
        GraphRecording recording = new();
        BufferId working = recording.AddBuffer(
            new BufferDesc(64, BufferUsage.CopySource | BufferUsage.CopyDestination, "segmented-working"),
            default);
        BufferDesc outputDesc = new(4, BufferUsage.CopyDestination, "segmented-output");
        BufferHandle outputHandle = device.CreateBuffer(outputDesc);
        BufferId output = recording.AddBuffer(
            outputDesc,
            new ImportedBuffer(
                outputHandle,
                device.GetBufferMetadata(outputHandle),
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                ContentsAvailable: false));

        int broadProducer = recording.AddPass("produce-0-through-31", QueueSelection.Copy);
        _ = recording.AddBufferAccess(
            broadProducer,
            working,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            new BufferRange(0, 32),
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(broadProducer, Noop);

        int latestProducer = recording.AddPass("overwrite-0-through-15", QueueSelection.Copy);
        _ = recording.AddBufferAccess(
            latestProducer,
            working,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            new BufferRange(0, 16),
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(latestProducer, Noop);

        int disjointProducer = recording.AddPass("produce-32-through-47", QueueSelection.Copy);
        _ = recording.AddBufferAccess(
            disjointProducer,
            working,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            new BufferRange(32, 16),
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(disjointProducer, Noop);

        int root = recording.AddPass("consume-0-through-31", QueueSelection.Copy);
        _ = recording.AddBufferAccess(
            root,
            working,
            ResourceEffect.Read,
            BufferUse.CopySource,
            new BufferRange(0, 32),
            PriorContents.Required,
            WriteCoverage.Partial);
        _ = recording.AddBufferAccess(
            root,
            output,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(root, Noop);

        FrozenGraph frozen = recording.Freeze(device);
        CompiledGraph compiled = Compiler.Compile(frozen, device.Compilation, optimized: false);

        Assert.Equal([broadProducer, latestProducer, root], compiled.ActivePassOrdinals);
        Assert.Empty(compiled.Dependencies[disjointProducer]);
        Assert.True(compiled.LiveResources[working.Ordinal]);
        Assert.True(compiled.LiveResources[output.Ordinal]);

        device.DestroyBuffer(outputHandle);
    }

    [Fact]
    public void Texture_liveness_partitions_mip_layer_and_aspect()
    {
        using NullDevice device = new();
        GraphRecording recording = new();
        TextureId working = recording.AddTexture(
            new TextureDesc(
                16,
                16,
                Format.D24UNormS8UInt,
                TextureUsage.CopySource | TextureUsage.CopyDestination,
                MipLevels: 2,
                ArrayLayers: 2,
                Name: "partitioned-depth-stencil"),
            default);
        BufferDesc outputDesc = new(4, BufferUsage.CopyDestination, "texture-root-output");
        BufferHandle outputHandle = device.CreateBuffer(outputDesc);
        BufferId output = recording.AddBuffer(
            outputDesc,
            new ImportedBuffer(
                outputHandle,
                device.GetBufferMetadata(outputHandle),
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                ContentsAvailable: false));

        TextureSubresourceRange selected = new(0, 1, 0, 1, TextureAspect.Depth);
        int selectedProducer = AddTextureWrite(recording, "selected-depth", working, selected);
        int otherAspect = AddTextureWrite(
            recording,
            "same-mip-layer-stencil",
            working,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Stencil));
        int otherMip = AddTextureWrite(
            recording,
            "other-mip",
            working,
            new TextureSubresourceRange(1, 1, 0, 1, TextureAspect.Depth));
        int otherLayer = AddTextureWrite(
            recording,
            "other-layer",
            working,
            new TextureSubresourceRange(0, 1, 1, 1, TextureAspect.Depth));

        int root = recording.AddPass("consume-selected-depth", QueueSelection.Copy);
        _ = recording.AddTextureAccess(
            root,
            working,
            ResourceEffect.Read,
            TextureUse.CopySource,
            selected,
            PriorContents.Required,
            WriteCoverage.Partial);
        _ = recording.AddBufferAccess(
            root,
            output,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(root, Noop);

        CompiledGraph compiled = Compiler.Compile(recording.Freeze(device), device.Compilation, optimized: false);

        Assert.Equal([selectedProducer, root], compiled.ActivePassOrdinals);
        Assert.Empty(compiled.Dependencies[otherAspect]);
        Assert.Empty(compiled.Dependencies[otherMip]);
        Assert.Empty(compiled.Dependencies[otherLayer]);

        device.DestroyBuffer(outputHandle);
    }

    [Fact]
    public void Imported_writes_are_roots_but_imported_reads_are_not()
    {
        using NullDevice device = new();
        GraphRecording recording = new();
        BufferDesc sourceDesc = new(16, BufferUsage.CopySource, "read-only-import");
        BufferHandle sourceHandle = device.CreateBuffer(sourceDesc);
        BufferId source = recording.AddBuffer(
            sourceDesc,
            new ImportedBuffer(
                sourceHandle,
                device.GetBufferMetadata(sourceHandle),
                BufferUse.CopySource,
                BufferUse.CopySource,
                ContentsAvailable: true));
        BufferDesc destinationDesc = new(16, BufferUsage.CopyDestination, "written-import");
        BufferHandle destinationHandle = device.CreateBuffer(destinationDesc);
        BufferId destination = recording.AddBuffer(
            destinationDesc,
            new ImportedBuffer(
                destinationHandle,
                device.GetBufferMetadata(destinationHandle),
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                ContentsAvailable: false));

        int readOnly = recording.AddPass("read-import", QueueSelection.Copy);
        _ = recording.AddBufferAccess(
            readOnly,
            source,
            ResourceEffect.Read,
            BufferUse.CopySource,
            BufferRange.Whole,
            PriorContents.Required,
            WriteCoverage.Partial);
        recording.SetExecution(readOnly, Noop);

        int write = recording.AddPass("write-import", QueueSelection.Copy);
        _ = recording.AddBufferAccess(
            write,
            destination,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(write, Noop);

        CompiledGraph compiled = Compiler.Compile(recording.Freeze(device), device.Compilation, optimized: false);

        Assert.Equal([write], compiled.ActivePassOrdinals);
        Assert.False(compiled.LiveResources[source.Ordinal]);
        Assert.True(compiled.LiveResources[destination.Ordinal]);
        Assert.Empty(compiled.BeforeBarriers[readOnly]);
        Assert.Empty(compiled.AfterBarriers[readOnly]);

        device.DestroyBuffer(sourceHandle);
        device.DestroyBuffer(destinationHandle);
    }

    [Fact]
    public void Transient_only_graph_records_and_submits_nothing()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
        });
        int callbacks = 0;
        GraphBuilder builder = graph.Begin();
        BufferId transient = builder.CreateBuffer(
            new BufferDesc(64, BufferUsage.CopyDestination, "dead-transient"));
        PassBuilder pass = builder.AddPass("dead-producer", QueueSelection.Copy);
        _ = pass.Write(transient, BufferUse.CopyDestination);
        pass.Execute((ICommandContext _, in PassResources _) => Interlocked.Increment(ref callbacks));

        GraphExecution execution = graph.Execute(ref builder);

        Assert.Empty(execution.Completions);
        Assert.True(execution.Wait(TimeSpan.Zero));
        Assert.Equal(0, Volatile.Read(ref callbacks));
        Assert.Equal(0, graph.Statistics.CommandListsRecorded);
        Assert.Equal(0, graph.Statistics.Submissions);
        Assert.Equal(1, graph.Statistics.LastCulling.DeclaredPasses);
        Assert.Equal(0, graph.Statistics.LastCulling.LivePasses);
        Assert.Equal(1, graph.Statistics.LastCulling.CulledPasses);
        Assert.Equal(256UL, graph.Statistics.LastCulling.CulledTransientBytes);
        Assert.Equal(0, device.Statistics.HeapCreates);
        Assert.Equal(0, device.Statistics.BufferCreates);
    }

    [Fact]
    public void Dead_import_access_does_not_consume_readiness_and_final_transition_belongs_to_live_root()
    {
        using NullDevice device = new(new NullOptions { AutoCompleteSubmissions = false });
        BufferDesc desc = new(
            64,
            BufferUsage.CopySource | BufferUsage.CopyDestination,
            "ready-import");
        BufferHandle handle = device.CreateBuffer(desc);
        GpuCompletion producer = Transition(
            device,
            QueueType.Copy,
            handle.Resource,
            ResourceState.Common,
            ResourceState.CopySource);
        GraphRecording recording = new();
        BufferId imported = recording.AddBuffer(
            desc,
            new ImportedBuffer(
                handle,
                device.GetBufferMetadata(handle),
                BufferUse.CopySource,
                BufferUse.CopySource,
                ContentsAvailable: true,
                Readiness: [producer]));
        int deadCallbacks = 0;
        int liveCallbacks = 0;

        int deadRead = recording.AddPass("dead-read", QueueSelection.Compute);
        _ = recording.AddBufferAccess(
            deadRead,
            imported,
            ResourceEffect.Read,
            BufferUse.CopySource,
            BufferRange.Whole,
            PriorContents.Required,
            WriteCoverage.Partial);
        recording.SetExecution(
            deadRead,
            (ICommandContext _, in PassResources _) => Interlocked.Increment(ref deadCallbacks));

        int liveRoot = recording.AddPass("live-overwrite", QueueSelection.Graphics);
        _ = recording.AddBufferAccess(
            liveRoot,
            imported,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(
            liveRoot,
            (ICommandContext _, in PassResources _) => Interlocked.Increment(ref liveCallbacks));

        FrozenGraph frozen = recording.Freeze(device);
        GpuCompletion[] graphCompletions;
        using (CompilationCache cache = new(device, 8, 1024 * 1024, false, static _ => { }))
        {
            CompiledGraphLease lease = cache.Acquire(frozen, device.Compilation);
            CompiledGraph compiled = lease.Graph;
            try
            {
                Assert.Equal([liveRoot], compiled.ActivePassOrdinals);
                Assert.Empty(compiled.BeforeBarriers[deadRead]);
                Assert.Empty(compiled.AfterBarriers[deadRead]);

                BarrierTemplate before = Assert.Single(compiled.BeforeBarriers[liveRoot]);
                Assert.Equal(BarrierKind.Transition, before.Kind);
                Assert.Equal(ResourceState.CopySource, before.Before);
                Assert.Equal(ResourceState.CopyDestination, before.After);
                Assert.Empty(compiled.AfterBarriers[liveRoot]);

                CompiledRecordUnit finalizer = Assert.Single(
                    compiled.RecordUnits,
                    static unit => unit.Kind == CompiledRecordUnitKind.InternalBarriers);
                Assert.Equal(QueueType.Graphics, finalizer.Queue);
                BarrierTemplate after = Assert.Single(finalizer.InternalBarriers);
                Assert.Equal(BarrierKind.Transition, after.Kind);
                Assert.Equal(ResourceState.CopyDestination, after.Before);
                Assert.Equal(ResourceState.CopySource, after.After);
            }
            catch
            {
                lease.Release();
                throw;
            }

            GraphInvocation invocation = GraphInvocation.Realize(device, frozen, lease);
            graphCompletions = invocation.RecordAndSubmit();
        }

        Assert.Equal(0, Volatile.Read(ref deadCallbacks));
        Assert.Equal(1, Volatile.Read(ref liveCallbacks));
        Assert.Equal(2, device.Statistics.Submissions);
        Assert.Equal(1, device.Statistics.SubmissionWaits);
        Assert.Single(graphCompletions);

        device.DestroyBuffer(handle);
        device.AdvanceCompletion(producer);
        foreach (GpuCompletion completion in graphCompletions) device.AdvanceCompletion(completion);
        Assert.True(device.CollectGarbage() >= 1);
    }

    [Fact]
    public void Conservative_and_optimized_compilation_share_identical_liveness_masks()
    {
        using NullDevice device = new();
        GraphRecording recording = new();
        BufferDesc transientBufferDesc = new(
            32,
            BufferUsage.CopyDestination | BufferUsage.ShaderRead,
            "mask-buffer");
        BufferId liveBuffer = recording.AddBuffer(transientBufferDesc with { Name = "live-buffer" }, default);
        BufferId deadBuffer = recording.AddBuffer(transientBufferDesc with { Name = "dead-buffer" }, default);
        BufferViewId liveBufferView = recording.AddBufferView(
            liveBuffer,
            BufferRange.Whole,
            BindingKind.ReadOnlyBuffer,
            Format.Unknown,
            0,
            "live-buffer-view");
        BufferViewId deadBufferView = recording.AddBufferView(
            deadBuffer,
            BufferRange.Whole,
            BindingKind.ReadOnlyBuffer,
            Format.Unknown,
            0,
            "dead-buffer-view");

        TextureDesc transientTextureDesc = new(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopyDestination | TextureUsage.Sampled,
            Name: "mask-texture");
        TextureId liveTexture = recording.AddTexture(transientTextureDesc with { Name = "live-texture" }, default);
        TextureId deadTexture = recording.AddTexture(transientTextureDesc with { Name = "dead-texture" }, default);
        TextureViewId liveTextureView = recording.AddTextureView(
            liveTexture,
            TextureSubresourceRange.WholeColor,
            TextureViewUsage.ShaderResource,
            Format.Unknown,
            "live-texture-view");
        TextureViewId deadTextureView = recording.AddTextureView(
            deadTexture,
            TextureSubresourceRange.WholeColor,
            TextureViewUsage.ShaderResource,
            Format.Unknown,
            "dead-texture-view");

        BufferDesc outputDesc = new(4, BufferUsage.CopyDestination, "mask-output");
        BufferHandle outputHandle = device.CreateBuffer(outputDesc);
        BufferId output = recording.AddBuffer(
            outputDesc,
            new ImportedBuffer(
                outputHandle,
                device.GetBufferMetadata(outputHandle),
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                ContentsAvailable: false));

        int liveProducer = recording.AddPass("live-producer", QueueSelection.Copy);
        _ = recording.AddBufferAccess(
            liveProducer,
            liveBuffer,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        _ = recording.AddTextureAccess(
            liveProducer,
            liveTexture,
            ResourceEffect.Write,
            TextureUse.CopyDestination,
            TextureSubresourceRange.WholeColor,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(liveProducer, Noop);

        int liveRoot = recording.AddPass("live-root", QueueSelection.Graphics);
        _ = recording.AddBufferViewAccess(
            liveRoot,
            liveBufferView,
            ResourceEffect.Read,
            PriorContents.Required,
            WriteCoverage.Partial);
        _ = recording.AddTextureViewAccess(
            liveRoot,
            liveTextureView,
            ResourceEffect.Read,
            PriorContents.Required,
            WriteCoverage.Partial);
        _ = recording.AddBufferAccess(
            liveRoot,
            output,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(liveRoot, Noop);

        int deadProducer = recording.AddPass("dead-producer", QueueSelection.Copy);
        _ = recording.AddBufferAccess(
            deadProducer,
            deadBuffer,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        _ = recording.AddTextureAccess(
            deadProducer,
            deadTexture,
            ResourceEffect.Write,
            TextureUse.CopyDestination,
            TextureSubresourceRange.WholeColor,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(deadProducer, Noop);

        int deadConsumer = recording.AddPass("dead-consumer", QueueSelection.Graphics);
        _ = recording.AddBufferViewAccess(
            deadConsumer,
            deadBufferView,
            ResourceEffect.Read,
            PriorContents.Required,
            WriteCoverage.Partial);
        _ = recording.AddTextureViewAccess(
            deadConsumer,
            deadTextureView,
            ResourceEffect.Read,
            PriorContents.Required,
            WriteCoverage.Partial);
        recording.SetExecution(deadConsumer, Noop);

        FrozenGraph frozen = recording.Freeze(device);
        CompiledGraph conservative = Compiler.Compile(frozen, device.Compilation, optimized: false);
        CompiledGraph optimized = Compiler.Compile(frozen.DetachForCompilation(), device.Compilation, optimized: true);

        Assert.False(conservative.Optimized);
        Assert.True(optimized.Optimized);
        Assert.Equal([liveProducer, liveRoot], conservative.ActivePassOrdinals);
        Assert.Equal(conservative.ActivePassOrdinals, optimized.ActivePassOrdinals);
        Assert.Equal([true, false, true, false, true], conservative.LiveResources);
        Assert.Equal(conservative.LiveResources, optimized.LiveResources);
        Assert.Equal([true, false], conservative.LiveBufferViews);
        Assert.Equal(conservative.LiveBufferViews, optimized.LiveBufferViews);
        Assert.Equal([true, false], conservative.LiveTextureViews);
        Assert.Equal(conservative.LiveTextureViews, optimized.LiveTextureViews);

        device.DestroyBuffer(outputHandle);
    }

    private static int AddTextureWrite(
        GraphRecording recording,
        string name,
        TextureId texture,
        TextureSubresourceRange range)
    {
        int pass = recording.AddPass(name, QueueSelection.Copy);
        _ = recording.AddTextureAccess(
            pass,
            texture,
            ResourceEffect.Write,
            TextureUse.CopyDestination,
            range,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(pass, Noop);
        return pass;
    }

    private static GpuCompletion Transition(
        NullDevice device,
        QueueType queue,
        ResourceHandle resource,
        ResourceState before,
        ResourceState after)
    {
        using ICommandContext commands = device.AcquireCommandContext(queue);
        commands.Barriers([ResourceBarrier.Transition(resource, before, after)]);
        return device.Submit(queue, [commands.Finish()]);
    }
}
